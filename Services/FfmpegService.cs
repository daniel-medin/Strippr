using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.ComponentModel;
using Microsoft.Extensions.Options;
using Strippr.Models;
using Strippr.Options;

namespace Strippr.Services;

public sealed partial class FfmpegService
{
    private readonly FfmpegBootstrapper _bootstrapper;
    private readonly ILogger<FfmpegService> _logger;
    private readonly StripprOptions _options;
    private string? _resolvedExecutablePath;

    public FfmpegService(
        FfmpegBootstrapper bootstrapper,
        ILogger<FfmpegService> logger,
        IOptions<StripprOptions> options)
    {
        _bootstrapper = bootstrapper;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<FfmpegHealthStatus> GetHealthStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var executablePath = ResolveExecutablePath();
            var result = await RunProcessAsync(
                executablePath,
                arguments: ["-version"],
                cancellationToken: cancellationToken);

            if (result.ExitCode == 0)
            {
                var versionLine = result.StandardOutput
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();

                return new FfmpegHealthStatus(true, versionLine ?? "FFmpeg is available.");
            }

            return new FfmpegHealthStatus(false, "FFmpeg was found but did not respond correctly.");
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            _logger.LogWarning(exception, "FFmpeg is not available at path {Path}", _options.FfmpegPath);
            return new FfmpegHealthStatus(false, $"FFmpeg was not found on PATH. Expected executable: {_options.FfmpegPath}");
        }
    }

    public async Task<MediaAnalysis> AnalyzeSilenceAsync(
        string inputPath,
        string noiseThreshold,
        double minimumSilenceSeconds,
        CancellationToken cancellationToken)
    {
        var executablePath = ResolveExecutablePath();
        var result = await RunProcessAsync(
            executablePath,
            arguments:
            [
                "-hide_banner",
                "-i", inputPath,
                "-af", $"silencedetect=noise={noiseThreshold}:d={minimumSilenceSeconds.ToString("0.###", CultureInfo.InvariantCulture)}",
                "-f", "null",
                "-"
            ],
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg analysis failed: {result.StandardError}");
        }

        var durationSeconds = ParseDuration(result.StandardError);
        var silenceIntervals = ParseSilenceIntervals(result.StandardError, durationSeconds);

        return new MediaAnalysis(durationSeconds, silenceIntervals);
    }

    public async Task RenderWithoutSilenceAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<KeepSegment> keepSegments,
        CancellationToken cancellationToken)
    {
        var executablePath = ResolveExecutablePath();
        var filter = BuildConcatFilter(keepSegments);

        var result = await RunProcessAsync(
            executablePath,
            arguments:
            [
                "-y",
                "-hide_banner",
                "-i", inputPath,
                "-filter_complex", filter,
                "-map", "[v]",
                "-map", "[a]",
                "-movflags", "+faststart",
                outputPath
            ],
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg render failed: {result.StandardError}");
        }
    }

    private async Task<ProcessResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private string ResolveExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_resolvedExecutablePath))
        {
            return _resolvedExecutablePath;
        }

        if (File.Exists(_options.FfmpegPath))
        {
            _resolvedExecutablePath = _options.FfmpegPath;
            return _resolvedExecutablePath;
        }

        _resolvedExecutablePath = _bootstrapper.GetBundledExecutablePath()
            ?? FindWingetInstallPath()
            ?? _options.FfmpegPath;
        return _resolvedExecutablePath;
    }

    private static string? FindWingetInstallPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        var packagesRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packagesRoot))
        {
            return null;
        }

        var packageDirectories = Directory.GetDirectories(packagesRoot, "Gyan.FFmpeg.Essentials*")
            .Concat(Directory.GetDirectories(packagesRoot, "Gyan.FFmpeg*"))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var packageDirectory in packageDirectories)
        {
            var candidate = Directory
                .EnumerateFiles(packageDirectory, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static double ParseDuration(string ffmpegOutput)
    {
        var match = DurationRegex().Match(ffmpegOutput);

        if (!match.Success)
        {
            throw new InvalidOperationException("Unable to determine media duration from FFmpeg output.");
        }

        return TimeSpan.ParseExact(match.Groups["value"].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture)
            .TotalSeconds;
    }

    private static IReadOnlyList<SilenceInterval> ParseSilenceIntervals(string ffmpegOutput, double durationSeconds)
    {
        var intervals = new List<SilenceInterval>();
        double? currentStart = null;

        using var reader = new StringReader(ffmpegOutput);

        while (reader.ReadLine() is { } line)
        {
            var startMatch = SilenceStartRegex().Match(line);
            if (startMatch.Success)
            {
                currentStart = ParseDouble(startMatch.Groups["value"].Value);
                continue;
            }

            var endMatch = SilenceEndRegex().Match(line);
            if (!endMatch.Success || currentStart is null)
            {
                continue;
            }

            var endSeconds = ParseDouble(endMatch.Groups["value"].Value);
            intervals.Add(new SilenceInterval(currentStart.Value, endSeconds));
            currentStart = null;
        }

        if (currentStart is not null)
        {
            intervals.Add(new SilenceInterval(currentStart.Value, durationSeconds));
        }

        return intervals;
    }

    private static string BuildConcatFilter(IReadOnlyList<KeepSegment> keepSegments)
    {
        if (keepSegments.Count == 0)
        {
            throw new InvalidOperationException("At least one keep segment is required for rendering.");
        }

        if (keepSegments.Count == 1)
        {
            var segment = keepSegments[0];
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[0:v]trim=start={segment.StartSeconds:0.###}:end={segment.EndSeconds:0.###},setpts=PTS-STARTPTS[v];" +
                $"[0:a]atrim=start={segment.StartSeconds:0.###}:end={segment.EndSeconds:0.###},asetpts=PTS-STARTPTS[a]");
        }

        var builder = new StringBuilder();

        for (var index = 0; index < keepSegments.Count; index++)
        {
            var segment = keepSegments[index];
            builder.Append("[0:v]trim=start=");
            builder.Append(segment.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(":end=");
            builder.Append(segment.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(",setpts=PTS-STARTPTS[v");
            builder.Append(index);
            builder.Append("];");

            builder.Append("[0:a]atrim=start=");
            builder.Append(segment.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(":end=");
            builder.Append(segment.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(",asetpts=PTS-STARTPTS[a");
            builder.Append(index);
            builder.Append("];");
        }

        for (var index = 0; index < keepSegments.Count; index++)
        {
            builder.Append("[v");
            builder.Append(index);
            builder.Append("][a");
            builder.Append(index);
            builder.Append(']');
        }

        builder.Append("concat=n=");
        builder.Append(keepSegments.Count);
        builder.Append(":v=1:a=1[v][a]");
        return builder.ToString();
    }

    private static double ParseDouble(string value)
    {
        return double.Parse(value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"Duration:\s(?<value>\d{2}:\d{2}:\d{2}\.\d{2})", RegexOptions.Compiled)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"silence_start:\s(?<value>\d+(?:\.\d+)?)", RegexOptions.Compiled)]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s(?<value>\d+(?:\.\d+)?)", RegexOptions.Compiled)]
    private static partial Regex SilenceEndRegex();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
