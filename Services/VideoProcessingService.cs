using Microsoft.Extensions.Options;
using Strippr.Models;
using Strippr.Options;

namespace Strippr.Services;

public sealed class VideoProcessingService
{
    private readonly CutPlanBuilder _cutPlanBuilder;
    private readonly FfmpegService _ffmpegService;
    private readonly ILogger<VideoProcessingService> _logger;
    private readonly StripprOptions _options;
    private readonly WorkspaceService _workspaceService;

    public VideoProcessingService(
        CutPlanBuilder cutPlanBuilder,
        FfmpegService ffmpegService,
        ILogger<VideoProcessingService> logger,
        IOptions<StripprOptions> options,
        WorkspaceService workspaceService)
    {
        _cutPlanBuilder = cutPlanBuilder;
        _ffmpegService = ffmpegService;
        _logger = logger;
        _options = options.Value;
        _workspaceService = workspaceService;
    }

    public async Task<VideoProcessingResult> ProcessAsync(
        IFormFile video,
        string noiseThreshold,
        double minimumSilenceSeconds,
        CancellationToken cancellationToken)
    {
        var health = await _ffmpegService.GetHealthStatusAsync(cancellationToken);
        if (!health.IsAvailable)
        {
            return new VideoProcessingResult(
                Success: false,
                Message: health.Message,
                OriginalFileName: video.FileName,
                OutputFileName: null,
                OutputPath: null,
                OutputUrl: null,
                SourceDurationSeconds: 0,
                OutputDurationSeconds: 0,
                RemovedDurationSeconds: 0,
                SilenceSegmentsDetected: 0,
                CutsApplied: false);
        }

        var maxUploadBytes = _options.MaxUploadMegabytes * 1024 * 1024;
        if (video.Length == 0)
        {
            return BuildFailure(video.FileName, "The uploaded file was empty.");
        }

        if (video.Length > maxUploadBytes)
        {
            return BuildFailure(video.FileName, $"The file exceeds the {_options.MaxUploadMegabytes} MB upload limit.");
        }

        var uploadPath = _workspaceService.CreateUploadPath(video.FileName);
        await using (var uploadStream = File.Create(uploadPath))
        {
            await video.CopyToAsync(uploadStream, cancellationToken);
        }

        try
        {
            var analysis = await _ffmpegService.AnalyzeSilenceAsync(
                uploadPath,
                noiseThreshold,
                minimumSilenceSeconds,
                cancellationToken);

            var cutPlan = _cutPlanBuilder.Build(
                analysis.DurationSeconds,
                analysis.SilenceIntervals,
                _options.MinimumKeepSegmentSeconds);

            if (cutPlan.RemovedSegments.Count == 0)
            {
                var passthroughOutputPath = _workspaceService.CreateOutputPath(video.FileName, useMp4Extension: false);
                File.Copy(uploadPath, passthroughOutputPath, overwrite: true);

                return new VideoProcessingResult(
                    Success: true,
                    Message: "No silence matched the current thresholds, so the original file was copied as-is.",
                    OriginalFileName: video.FileName,
                    OutputFileName: Path.GetFileName(passthroughOutputPath),
                    OutputPath: passthroughOutputPath,
                    OutputUrl: _workspaceService.GetOutputUrl(passthroughOutputPath),
                    SourceDurationSeconds: analysis.DurationSeconds,
                    OutputDurationSeconds: analysis.DurationSeconds,
                    RemovedDurationSeconds: 0,
                    SilenceSegmentsDetected: 0,
                    CutsApplied: false);
            }

            if (cutPlan.KeepSegments.Count == 0)
            {
                return BuildFailure(video.FileName, "Everything was classified as silence. Lower the silence threshold and try again.");
            }

            var outputPath = _workspaceService.CreateOutputPath(video.FileName, useMp4Extension: true);
            await _ffmpegService.RenderWithoutSilenceAsync(
                uploadPath,
                outputPath,
                cutPlan.KeepSegments,
                cancellationToken);

            return new VideoProcessingResult(
                Success: true,
                Message: "Video processed successfully.",
                OriginalFileName: video.FileName,
                OutputFileName: Path.GetFileName(outputPath),
                OutputPath: outputPath,
                OutputUrl: _workspaceService.GetOutputUrl(outputPath),
                SourceDurationSeconds: cutPlan.SourceDurationSeconds,
                OutputDurationSeconds: cutPlan.OutputDurationSeconds,
                RemovedDurationSeconds: cutPlan.RemovedDurationSeconds,
                SilenceSegmentsDetected: cutPlan.RemovedSegments.Count,
                CutsApplied: true);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Processing failed for file {FileName}", video.FileName);
            return BuildFailure(video.FileName, exception.Message);
        }
    }

    private static VideoProcessingResult BuildFailure(string originalFileName, string message)
    {
        return new VideoProcessingResult(
            Success: false,
            Message: message,
            OriginalFileName: originalFileName,
            OutputFileName: null,
            OutputPath: null,
            OutputUrl: null,
            SourceDurationSeconds: 0,
            OutputDurationSeconds: 0,
            RemovedDurationSeconds: 0,
            SilenceSegmentsDetected: 0,
            CutsApplied: false);
    }
}
