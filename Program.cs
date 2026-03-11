using System.Diagnostics;
using System.Net.Mime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Strippr.Options;
using Strippr.Services;

var builder = WebApplication.CreateBuilder(args);

var maxUploadMegabytes = builder.Configuration.GetValue<long?>("Strippr:MaxUploadMegabytes") ?? 1024;
var maxUploadBytes = maxUploadMegabytes * 1024 * 1024;

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

builder.Services.Configure<StripprOptions>(builder.Configuration.GetSection(StripprOptions.SectionName));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});
builder.Services.AddHttpClient<FfmpegBootstrapper>();
builder.Services.AddRazorPages();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<FfmpegService>();
builder.Services.AddSingleton<CutPlanBuilder>();
builder.Services.AddScoped<VideoProcessingService>();

var app = builder.Build();
var workspace = app.Services.GetRequiredService<WorkspaceService>();
workspace.EnsureCreated();

try
{
    var ffmpegBootstrapper = app.Services.GetRequiredService<FfmpegBootstrapper>();
    await ffmpegBootstrapper.EnsureInstalledAsync(CancellationToken.None);
}
catch (Exception exception)
{
    app.Logger.LogWarning(exception, "Bundled FFmpeg bootstrap failed. The app will continue and use any system FFmpeg if available.");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapGet("/download/{fileName}", async Task<IResult> (
    string fileName,
    HttpContext httpContext,
    ILogger<Program> logger,
    WorkspaceService workspaceService,
    CancellationToken cancellationToken) =>
{
    var outputPath = workspaceService.TryGetOutputPath(fileName);
    if (outputPath is null)
    {
        return Results.NotFound();
    }

    var contentTypeProvider = new FileExtensionContentTypeProvider();
    if (!contentTypeProvider.TryGetContentType(outputPath, out var contentType))
    {
        contentType = "application/octet-stream";
    }

    var downloadName = Path.GetFileName(outputPath);

    try
    {
        await using (var fileStream = File.OpenRead(outputPath))
        {
            httpContext.Response.ContentType = contentType;
            httpContext.Response.ContentLength = fileStream.Length;
            httpContext.Response.Headers.ContentDisposition = new ContentDisposition
            {
                FileName = downloadName,
                Inline = false
            }.ToString();

            await fileStream.CopyToAsync(httpContext.Response.Body, cancellationToken);
        }
    }
    finally
    {
        try
        {
            workspaceService.TryDeleteOutput(downloadName);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to delete temporary output file {FileName}", downloadName);
        }
    }

    return Results.Empty;
});
app.MapRazorPages()
   .WithStaticAssets();

var stripprOptions = app.Services.GetRequiredService<IOptions<StripprOptions>>().Value;
var shouldLaunchBrowser =
    stripprOptions.LaunchBrowserOnStartup &&
    !app.Environment.IsDevelopment() &&
    Environment.UserInteractive;

await app.StartAsync();

if (shouldLaunchBrowser)
{
    TryLaunchBrowser(app);
}

await app.WaitForShutdownAsync();

static void TryLaunchBrowser(WebApplication app)
{
    var launchUrl = GetLaunchUrl(app);
    if (launchUrl is null)
    {
        app.Logger.LogWarning("The app started, but no launchable local address was found for browser startup.");
        return;
    }

    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = launchUrl,
            UseShellExecute = true
        });
        app.Logger.LogInformation("Opened browser to {Url}", launchUrl);
    }
    catch (Exception exception)
    {
        app.Logger.LogWarning(exception, "Failed to open browser to {Url}", launchUrl);
    }
}

static string? GetLaunchUrl(WebApplication app)
{
    var addresses = app.Services
        .GetRequiredService<IServer>()
        .Features
        .Get<IServerAddressesFeature>()?
        .Addresses;

    if (addresses is null)
    {
        return null;
    }

    foreach (var address in addresses.OrderByDescending(address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
    {
        if (TryNormalizeLaunchUrl(address, out var launchUrl))
        {
            return launchUrl;
        }
    }

    return null;
}

static bool TryNormalizeLaunchUrl(string address, out string? launchUrl)
{
    launchUrl = null;

    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
        uri.Scheme is not ("http" or "https"))
    {
        return false;
    }

    var uriBuilder = new UriBuilder(uri);

    if (uriBuilder.Host is "*" or "+" or "0.0.0.0" or "[::]" or "::")
    {
        uriBuilder.Host = "127.0.0.1";
    }

    launchUrl = uriBuilder.Uri.AbsoluteUri;
    return true;
}
