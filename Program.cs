using System.Diagnostics;
using System.Globalization;
using System.Net.Mime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Strippr.Models;
using Strippr.Options;
using Strippr.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

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
builder.Services.AddHttpClient<OpenAiSilenceAnalysisService>();
builder.Services.AddRazorPages();
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<FfmpegService>();
builder.Services.AddSingleton<SileroVadService>();
builder.Services.AddSingleton<CutPlanBuilder>();
builder.Services.AddSingleton<AiFeedbackStore>();
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
app.MapGet("/api/silero/status", (SileroVadService sileroVadService) =>
{
    var status = sileroVadService.GetStatus();
    return Results.Ok(new
    {
        success = true,
        enabled = status.Enabled,
        runtimeAvailable = status.RuntimeAvailable,
        modelFileExists = status.ModelFileExists,
        modelPath = status.ModelPath,
        sampleRate = status.SampleRate,
        speechThreshold = status.SpeechThreshold,
        negativeSpeechThreshold = status.NegativeSpeechThreshold,
        minSpeechMilliseconds = status.MinSpeechMilliseconds,
        minSilenceMilliseconds = status.MinSilenceMilliseconds,
        speechPadMilliseconds = status.SpeechPadMilliseconds,
        message = status.Message
    });
});
app.MapPost("/api/silero/compare", async Task<IResult> (
    HttpRequest request,
    SileroVadService sileroVadService,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Silero comparison expects multipart form data."
        });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var video = form.Files.GetFile("video");
    var noiseThreshold = form["noiseThreshold"].ToString();
    var minimumSilenceValue = form["minimumSilenceSeconds"].ToString();

    if (video is null)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Choose a video file before running Silero comparison."
        });
    }

    double? minimumSilenceSeconds = null;
    if (double.TryParse(minimumSilenceValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMinimumSilenceSeconds) &&
        double.IsFinite(parsedMinimumSilenceSeconds))
    {
        minimumSilenceSeconds = parsedMinimumSilenceSeconds;
    }

    try
    {
        var result = await sileroVadService.CompareAsync(
            video,
            noiseThreshold,
            minimumSilenceSeconds,
            cancellationToken);

        return Results.Ok(new
        {
            success = true,
            durationSeconds = result.DurationSeconds,
            ffmpegNoiseThreshold = result.FfmpegNoiseThreshold,
            ffmpegMinimumSilenceSeconds = result.FfmpegMinimumSilenceSeconds,
            message = result.Message,
            silero = new
            {
                durationSeconds = result.Silero.DurationSeconds,
                sampleRate = result.Silero.SampleRate,
                sampleCount = result.Silero.SampleCount,
                message = result.Silero.Message,
                speechIntervals = result.Silero.SpeechIntervals.Select(interval => new
                {
                    startSeconds = interval.StartSeconds,
                    endSeconds = interval.EndSeconds
                }),
                silenceIntervals = result.Silero.SilenceIntervals.Select(interval => new
                {
                    startSeconds = interval.StartSeconds,
                    endSeconds = interval.EndSeconds
                })
            },
            ffmpegSilenceIntervals = result.FfmpegSilenceIntervals.Select(interval => new
            {
                startSeconds = interval.StartSeconds,
                endSeconds = interval.EndSeconds
            })
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = exception.Message
        });
    }
});
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
app.MapPost("/api/ai/silence-analysis", async Task<IResult> (
    HttpRequest request,
    OpenAiSilenceAnalysisService openAiSilenceAnalysisService,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "AI analysis expects multipart form data."
        });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var video = form.Files.GetFile("video");
    var apiKey = form["apiKey"].ToString();
    var model = form["model"].ToString();
    var language = form["language"].ToString();
    var minimumGapValue = form["minimumGapSeconds"].ToString();

    if (video is null)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Choose a video file before running AI analysis."
        });
    }

    if (!double.TryParse(minimumGapValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var minimumGapSeconds) ||
        !double.IsFinite(minimumGapSeconds))
    {
        minimumGapSeconds = 0.5;
    }

    try
    {
        var result = await openAiSilenceAnalysisService.AnalyzeAsync(
            video,
            apiKey,
            model,
            language,
            minimumGapSeconds,
            cancellationToken);

        return Results.Ok(new
        {
            success = true,
            model = result.Model,
            durationSeconds = result.DurationSeconds,
            message = result.Message,
            ranges = result.SilenceRanges.Select(range => new
            {
                startSeconds = range.StartSeconds,
                endSeconds = range.EndSeconds
            })
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = exception.Message
        });
    }
});
app.MapPost("/api/ai/content-analysis", async Task<IResult> (
    HttpRequest request,
    OpenAiSilenceAnalysisService openAiSilenceAnalysisService,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "A2 analysis expects multipart form data."
        });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var video = form.Files.GetFile("video");
    var apiKey = form["apiKey"].ToString();
    var analysisModel = form["analysisModel"].ToString();
    var language = form["language"].ToString();
    var useMemory = !string.Equals(form["useMemory"].ToString(), "false", StringComparison.OrdinalIgnoreCase);

    if (video is null)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Choose a video file before running A2 analysis."
        });
    }

    try
    {
        var result = await openAiSilenceAnalysisService.AnalyzeContentAsync(
            video,
            apiKey,
            analysisModel,
            language,
            useMemory,
            cancellationToken);

        return Results.Ok(new
        {
            success = true,
            transcriptionModel = result.TranscriptionModel,
            analysisModel = result.AnalysisModel,
            durationSeconds = result.DurationSeconds,
            message = result.Message,
            summary = result.Summary,
            transcriptText = result.TranscriptText,
            transcriptSegments = result.TranscriptSegments.Select(segment => new
            {
                startSeconds = segment.StartSeconds,
                endSeconds = segment.EndSeconds,
                text = segment.Text
            }),
            issues = result.Issues.Select(issue => new
            {
                startSeconds = issue.StartSeconds,
                endSeconds = issue.EndSeconds,
                label = issue.Label,
                reason = issue.Reason,
                excerpt = issue.Excerpt,
                confidence = issue.Confidence,
                isLearned = issue.IsLearned
            })
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = exception.Message
        });
    }
});
app.MapPost("/api/ai/auto-cut-analysis", async Task<IResult> (
    HttpRequest request,
    OpenAiSilenceAnalysisService openAiSilenceAnalysisService,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "A3 analysis expects multipart form data."
        });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var video = form.Files.GetFile("video");
    var apiKey = form["apiKey"].ToString();
    var analysisModel = form["analysisModel"].ToString();
    var language = form["language"].ToString();
    var targetText = form["targetText"].ToString();

    if (video is null)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = "Choose a video file before running A3 analysis."
        });
    }

    try
    {
        var result = await openAiSilenceAnalysisService.AnalyzeAutoCutAsync(
            video,
            apiKey,
            analysisModel,
            language,
            targetText,
            cancellationToken);

        return Results.Ok(new
        {
            success = true,
            transcriptionModel = result.TranscriptionModel,
            analysisModel = result.AnalysisModel,
            durationSeconds = result.DurationSeconds,
            message = result.Message,
            summary = result.Summary,
            transcriptText = result.TranscriptText,
            targetTranscript = result.TargetTranscript,
            resultTranscript = result.ResultTranscript,
            transcriptSegments = result.TranscriptSegments.Select(segment => new
            {
                startSeconds = segment.StartSeconds,
                endSeconds = segment.EndSeconds,
                text = segment.Text
            }),
            issues = result.Issues.Select(issue => new
            {
                startSeconds = issue.StartSeconds,
                endSeconds = issue.EndSeconds,
                label = issue.Label,
                reason = issue.Reason,
                excerpt = issue.Excerpt,
                confidence = issue.Confidence
            })
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = exception.Message
        });
    }
});
app.MapPost("/api/ai/content-memory/forget", async Task<IResult> (
    AiContentMemoryForgetRequest requestBody,
    AiFeedbackStore aiFeedbackStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var removedCount = await aiFeedbackStore.ForgetRememberedContentFeedbackAsync(
            requestBody.ClientFileName,
            requestBody.TranscriptText,
            cancellationToken);
        return Results.Ok(new
        {
            success = true,
            removedCount
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = exception.Message
        });
    }
});
app.MapPost("/api/ai/content-feedback", async Task<IResult> (
    AiContentFeedbackRequest requestBody,
    AiFeedbackStore aiFeedbackStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        await aiFeedbackStore.AppendContentFeedbackAsync(requestBody, cancellationToken);
        return Results.Ok(new
        {
            success = true
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new
        {
            success = false,
            message = exception.Message
        });
    }
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
