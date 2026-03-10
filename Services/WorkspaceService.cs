using System.Text;
using Microsoft.Extensions.Options;
using Strippr.Options;

namespace Strippr.Services;

public sealed class WorkspaceService
{
    private readonly string _contentRootPath;
    private readonly StripprOptions _options;

    public WorkspaceService(IWebHostEnvironment environment, IOptions<StripprOptions> options)
    {
        _contentRootPath = environment.ContentRootPath;
        _options = options.Value;
    }

    public string StorageRootPath => ResolvePath(_options.StorageRoot);

    public string UploadsPath => Path.Combine(StorageRootPath, _options.UploadsFolder);

    public string OutputsPath => ResolveOutputsPath();

    public string TempPath => Path.Combine(StorageRootPath, _options.TempFolder);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(StorageRootPath);
        Directory.CreateDirectory(UploadsPath);
        Directory.CreateDirectory(OutputsPath);
        Directory.CreateDirectory(TempPath);
    }

    public string CreateUploadPath(string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        var safeBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(originalFileName));
        var uniqueName = $"{safeBaseName}-{Guid.NewGuid():N}{extension}";
        return Path.Combine(UploadsPath, uniqueName);
    }

    public string CreateOutputPath(string originalFileName, bool useMp4Extension)
    {
        var safeBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(originalFileName));
        var extension = useMp4Extension ? ".mp4" : Path.GetExtension(originalFileName);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var uniqueName = $"{safeBaseName}-strippr-{DateTime.UtcNow:yyyyMMddHHmmss}-{suffix}{extension}";
        return Path.Combine(OutputsPath, uniqueName);
    }

    public string GetOutputUrl(string outputPath)
    {
        return $"/download/{Uri.EscapeDataString(Path.GetFileName(outputPath))}";
    }

    public string? TryGetOutputPath(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (!string.Equals(safeFileName, fileName, StringComparison.Ordinal))
        {
            return null;
        }

        var fullPath = Path.Combine(OutputsPath, safeFileName);
        return File.Exists(fullPath) ? fullPath : null;
    }

    private string ResolveOutputsPath()
    {
        if (Path.IsPathRooted(_options.OutputsFolder))
        {
            return _options.OutputsFolder;
        }

        var downloadsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        return Path.Combine(downloadsRoot, _options.OutputsFolder);
    }

    private string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(_contentRootPath, path);
    }

    private static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (character is '-' or '_')
            {
                builder.Append(character);
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                builder.Append('-');
            }
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "clip" : sanitized;
    }
}
