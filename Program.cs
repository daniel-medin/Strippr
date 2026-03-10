using System.Net.Mime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
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

app.Run();
