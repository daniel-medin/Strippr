using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
        var frameRate = await ProbeFrameRateAsync(inputPath, cancellationToken);
        var silenceIntervals = ParseSilenceIntervals(result.StandardError, durationSeconds);

        return new MediaAnalysis(durationSeconds, frameRate, silenceIntervals);
    }

    public async Task<MediaAnalysis> AnalyzeMediaAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        var durationSeconds = await ProbeDurationAsync(inputPath, cancellationToken);
        var frameRate = await ProbeFrameRateAsync(inputPath, cancellationToken);
        return new MediaAnalysis(durationSeconds, frameRate, []);
    }

    public async Task<double> RenderWithoutSilenceAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<RenderSegment> renderSegments,
        double crossfadeMilliseconds,
        int videoCrossfadeFrames,
        double frameRate,
        CancellationToken cancellationToken)
    {
        var executablePath = ResolveExecutablePath();
        var renderPlan = BuildRenderPlan(renderSegments, crossfadeMilliseconds, videoCrossfadeFrames, frameRate);

        var result = await RunProcessAsync(
            executablePath,
            arguments:
            [
                "-y",
                "-hide_banner",
                "-i", inputPath,
                "-filter_complex", renderPlan.Filter,
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

        return renderPlan.AppliedCrossfadeSeconds;
    }

    public async Task<double> ProbeDurationAsync(string inputPath, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            ResolveProbePath(),
            arguments:
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                inputPath
            ],
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFprobe duration probe failed: {result.StandardError}");
        }

        if (!double.TryParse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture, out var durationSeconds) ||
            !double.IsFinite(durationSeconds) ||
            durationSeconds <= 0)
        {
            throw new InvalidOperationException("Unable to read media duration from FFprobe output.");
        }

        return durationSeconds;
    }

    public async Task ExtractTranscriptionAudioAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var executablePath = ResolveExecutablePath();
        var result = await RunProcessAsync(
            executablePath,
            arguments:
            [
                "-y",
                "-hide_banner",
                "-i", inputPath,
                "-vn",
                "-ac", "1",
                "-ar", "16000",
                "-c:a", "libmp3lame",
                "-b:a", "24k",
                outputPath
            ],
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg transcription-audio extraction failed: {result.StandardError}");
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

    private string ResolveProbePath()
    {
        var executablePath = ResolveExecutablePath();
        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            var siblingProbePath = Path.Combine(executableDirectory, "ffprobe.exe");
            if (File.Exists(siblingProbePath))
            {
                return siblingProbePath;
            }
        }

        return "ffprobe";
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

    private async Task<double> ProbeFrameRateAsync(string inputPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunProcessAsync(
                ResolveProbePath(),
                arguments:
                [
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=avg_frame_rate,r_frame_rate",
                    "-of", "json",
                    inputPath
                ],
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return 30;
            }

            using var document = JsonDocument.Parse(result.StandardOutput);
            var streamElement = document.RootElement
                .GetProperty("streams")
                .EnumerateArray()
                .FirstOrDefault();

            var avgFrameRate = streamElement.TryGetProperty("avg_frame_rate", out var avgFrameRateElement)
                ? avgFrameRateElement.GetString()
                : null;
            var rawFrameRate = streamElement.TryGetProperty("r_frame_rate", out var rawFrameRateElement)
                ? rawFrameRateElement.GetString()
                : null;

            var parsedFrameRate = ParseFrameRate(rawFrameRate) ?? ParseFrameRate(avgFrameRate);
            return parsedFrameRate is > 0
                ? NormalizeFrameRate(parsedFrameRate.Value)
                : 30;
        }
        catch
        {
            return 30;
        }
    }

    private static RenderPlan BuildRenderPlan(
        IReadOnlyList<RenderSegment> renderSegments,
        double crossfadeMilliseconds,
        int videoCrossfadeFrames,
        double frameRate)
    {
        if (renderSegments.Count == 0)
        {
            throw new InvalidOperationException("At least one render segment is required for rendering.");
        }

        var requestedCrossfadeSeconds = Math.Max(0, crossfadeMilliseconds) / 1000d;
        var requestedVideoCrossfadeSeconds =
            videoCrossfadeFrames > 0 && frameRate > 0
                ? videoCrossfadeFrames / frameRate
                : 0;
        var appliedCrossfadeSeconds = DetermineAppliedCrossfadeSeconds(
            renderSegments,
            Math.Max(requestedCrossfadeSeconds, requestedVideoCrossfadeSeconds));
        var hardCutTransitionCount = renderSegments.Count(segment => segment.StartsAfterHardCut);

        if (renderSegments.Count == 1)
        {
            return new RenderPlan(
                BuildSegmentFilters(renderSegments[0], 0, frameRate) + "[v0]format=yuv420p[v];[a0]anull[a]",
                0);
        }

        var builder = new StringBuilder();
        for (var index = 0; index < renderSegments.Count; index++)
        {
            builder.Append(BuildSegmentFilters(renderSegments[index], index, frameRate));
        }

        var currentVideoLabel = "v0";
        var currentAudioLabel = "a0";
        var currentOutputDurationSeconds = renderSegments[0].OutputDurationSeconds;

        for (var index = 1; index < renderSegments.Count; index++)
        {
            var segment = renderSegments[index];
            var nextVideoLabel = $"v{index}";
            var nextAudioLabel = $"a{index}";
            var mergedVideoLabel = $"vm{index}";
            var mergedAudioLabel = $"am{index}";
            var useCrossfade = segment.StartsAfterHardCut && appliedCrossfadeSeconds > 0;

            if (useCrossfade)
            {
                builder.Append('[');
                builder.Append(currentVideoLabel);
                builder.Append("][");
                builder.Append(nextVideoLabel);
                builder.Append("]xfade=transition=fade:duration=");
                builder.Append(appliedCrossfadeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(":offset=");
                builder.Append(Math.Max(0, currentOutputDurationSeconds - appliedCrossfadeSeconds).ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append('[');
                builder.Append(mergedVideoLabel);
                builder.Append("];");

                builder.Append('[');
                builder.Append(currentAudioLabel);
                builder.Append("][");
                builder.Append(nextAudioLabel);
                builder.Append("]acrossfade=d=");
                builder.Append(appliedCrossfadeSeconds.ToString("0.###", CultureInfo.InvariantCulture));
                builder.Append(":c1=tri:c2=tri[");
                builder.Append(mergedAudioLabel);
                builder.Append("];");

                currentOutputDurationSeconds += segment.OutputDurationSeconds - appliedCrossfadeSeconds;
            }
            else
            {
                builder.Append('[');
                builder.Append(currentVideoLabel);
                builder.Append("][");
                builder.Append(nextVideoLabel);
                builder.Append("]concat=n=2:v=1:a=0[");
                builder.Append(mergedVideoLabel);
                builder.Append("];");

                builder.Append('[');
                builder.Append(currentAudioLabel);
                builder.Append("][");
                builder.Append(nextAudioLabel);
                builder.Append("]concat=n=2:v=0:a=1[");
                builder.Append(mergedAudioLabel);
                builder.Append("];");

                currentOutputDurationSeconds += segment.OutputDurationSeconds;
            }

            currentVideoLabel = mergedVideoLabel;
            currentAudioLabel = mergedAudioLabel;
        }

        builder.Append('[');
        builder.Append(currentVideoLabel);
        builder.Append("]format=yuv420p[v];[");
        builder.Append(currentAudioLabel);
        builder.Append("]anull[a]");

        return new RenderPlan(
            builder.ToString().TrimEnd(';'),
            appliedCrossfadeSeconds * hardCutTransitionCount);
    }

    private static string BuildSegmentFilters(RenderSegment segment, int index, double frameRate)
    {
        var builder = new StringBuilder();
        builder.Append("[0:v]trim=start=");
        builder.Append(segment.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(":end=");
        builder.Append(segment.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(",fps=");
        builder.Append(Math.Max(1, frameRate).ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(",settb=AVTB,setpts=");
        if (segment.PlaybackSpeed > 1)
        {
            builder.Append("(PTS-STARTPTS)/");
            builder.Append(segment.PlaybackSpeed.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else
        {
            builder.Append("PTS-STARTPTS");
        }

        builder.Append("[v");
        builder.Append(index);
        builder.Append("];");

        builder.Append("[0:a]atrim=start=");
        builder.Append(segment.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(":end=");
        builder.Append(segment.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        builder.Append(",asetpts=PTS-STARTPTS");

        if (segment.PlaybackSpeed > 1)
        {
            foreach (var atempoStep in BuildAtempoChain(segment.PlaybackSpeed))
            {
                builder.Append(",atempo=");
                builder.Append(atempoStep.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }

        builder.Append("[a");
        builder.Append(index);
        builder.Append("];");

        return builder.ToString();
    }

    private static double DetermineAppliedCrossfadeSeconds(
        IReadOnlyList<RenderSegment> renderSegments,
        double requestedCrossfadeSeconds)
    {
        if (requestedCrossfadeSeconds <= 0 || renderSegments.Count < 2)
        {
            return 0;
        }

        var maxAllowedCrossfadeSeconds = double.MaxValue;
        var foundHardCutTransition = false;

        for (var index = 1; index < renderSegments.Count; index++)
        {
            if (!renderSegments[index].StartsAfterHardCut)
            {
                continue;
            }

            foundHardCutTransition = true;
            var current = renderSegments[index - 1].OutputDurationSeconds;
            var next = renderSegments[index].OutputDurationSeconds;
            var allowedForPair = Math.Max(0, Math.Min(current, next) - 0.01);
            maxAllowedCrossfadeSeconds = Math.Min(maxAllowedCrossfadeSeconds, allowedForPair);
        }

        if (!foundHardCutTransition || maxAllowedCrossfadeSeconds <= 0)
        {
            return 0;
        }

        return Math.Min(requestedCrossfadeSeconds, maxAllowedCrossfadeSeconds);
    }

    private static IReadOnlyList<double> BuildAtempoChain(double playbackSpeed)
    {
        if (playbackSpeed <= 1)
        {
            return [1];
        }

        var steps = new List<double>();
        var remainingSpeed = playbackSpeed;

        while (remainingSpeed > 2)
        {
            steps.Add(2);
            remainingSpeed /= 2;
        }

        if (remainingSpeed > 1.001)
        {
            steps.Add(remainingSpeed);
        }

        return steps.Count > 0 ? steps : [1];
    }

    private static double? ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!value.Contains('/'))
        {
            return double.TryParse(value, CultureInfo.InvariantCulture, out var directFrameRate)
                ? directFrameRate
                : null;
        }

        var parts = value.Split('/', 2);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) ||
            denominator == 0)
        {
            return null;
        }

        return numerator / denominator;
    }

    private static double NormalizeFrameRate(double value)
    {
        if (value <= 0)
        {
            return 30;
        }

        var commonFrameRates = new[]
        {
            15d,
            23.976d,
            24d,
            25d,
            29.97d,
            30d,
            50d,
            59.94d,
            60d
        };

        foreach (var candidate in commonFrameRates)
        {
            if (Math.Abs(value - candidate) <= 0.25d)
            {
                return candidate;
            }
        }

        return Math.Round(value, 3);
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

    private sealed record RenderPlan(string Filter, double AppliedCrossfadeSeconds);
}
