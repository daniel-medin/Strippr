using System.IO.Compression;
using Microsoft.Extensions.Options;
using Strippr.Options;

namespace Strippr.Services;

public sealed class FfmpegBootstrapper
{
    private readonly string _contentRootPath;
    private readonly HttpClient _httpClient;
    private readonly ILogger<FfmpegBootstrapper> _logger;
    private readonly StripprOptions _options;

    public FfmpegBootstrapper(
        IWebHostEnvironment environment,
        HttpClient httpClient,
        ILogger<FfmpegBootstrapper> logger,
        IOptions<StripprOptions> options)
    {
        _contentRootPath = environment.ContentRootPath;
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
    }

    public string BundledRootPath => ResolvePath(_options.BundledFfmpegFolder);

    public string? GetBundledExecutablePath()
    {
        if (!Directory.Exists(BundledRootPath))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(BundledRootPath, "ffmpeg.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_options.FfmpegPath) || GetBundledExecutablePath() is not null)
        {
            return;
        }

        Directory.CreateDirectory(BundledRootPath);

        var archivePath = Path.Combine(BundledRootPath, "ffmpeg.zip");
        var extractPath = Path.Combine(BundledRootPath, "package");

        if (!File.Exists(archivePath))
        {
            _logger.LogInformation("Downloading bundled FFmpeg from {Url}", _options.FfmpegDownloadUrl);
            await using var downloadStream = await _httpClient.GetStreamAsync(_options.FfmpegDownloadUrl, cancellationToken);
            await using var fileStream = File.Create(archivePath);
            await downloadStream.CopyToAsync(fileStream, cancellationToken);
        }

        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, recursive: true);
        }

        ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);

        if (GetBundledExecutablePath() is null)
        {
            throw new InvalidOperationException("FFmpeg archive downloaded, but ffmpeg.exe was not found after extraction.");
        }
    }

    private string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(_contentRootPath, path);
    }
}
