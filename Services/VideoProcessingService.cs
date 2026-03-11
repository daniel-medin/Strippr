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
        bool automaticSilenceEnabled,
        string noiseThreshold,
        double minimumSilenceSeconds,
        double retainedSilenceSeconds,
        double cutHandleMilliseconds,
        double crossfadeMilliseconds,
        int videoCrossfadeFrames,
        double pauseSpeedMultiplier,
        IReadOnlyList<SilenceInterval> explicitCutRanges,
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
                RemovedSegmentsCount: 0,
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
            _workspaceService.DeleteUploadsExcept(uploadPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to remove superseded uploads after saving {UploadPath}", uploadPath);
        }

        try
        {
            var analysis = automaticSilenceEnabled
                ? await _ffmpegService.AnalyzeSilenceAsync(
                    uploadPath,
                    noiseThreshold,
                    minimumSilenceSeconds,
                    cancellationToken)
                : await _ffmpegService.AnalyzeMediaAsync(
                    uploadPath,
                    cancellationToken);

            var normalizedExplicitCutRanges = _cutPlanBuilder.Normalize(analysis.DurationSeconds, explicitCutRanges);
            var normalizedSilenceRanges = _cutPlanBuilder.Normalize(analysis.DurationSeconds, analysis.SilenceIntervals);
            var cutHandleSeconds = Math.Max(0, cutHandleMilliseconds) / 1000d;
            var handledSilenceRanges = _cutPlanBuilder.ApplyCutHandles(normalizedSilenceRanges, cutHandleSeconds);
            var useSilenceProcessing = pauseSpeedMultiplier > 1 || retainedSilenceSeconds > 0;
            var cutPlan = useSilenceProcessing
                ? _cutPlanBuilder.Build(
                    analysis.DurationSeconds,
                    normalizedExplicitCutRanges,
                    _options.MinimumKeepSegmentSeconds)
                : _cutPlanBuilder.Build(
                    analysis.DurationSeconds,
                    handledSilenceRanges.Concat(normalizedExplicitCutRanges).ToList(),
                    _options.MinimumKeepSegmentSeconds);
            var renderSegments = useSilenceProcessing
                ? _cutPlanBuilder.BuildRenderSegments(
                    analysis.DurationSeconds,
                    handledSilenceRanges,
                    normalizedExplicitCutRanges,
                    _options.MinimumKeepSegmentSeconds,
                    pauseSpeedMultiplier,
                    retainedSilenceSeconds)
                : cutPlan.KeepSegments
                    .Select((segment, index) => new RenderSegment(
                        segment.StartSeconds,
                        segment.EndSeconds,
                        PlaybackSpeed: 1,
                        StartsAfterHardCut: index > 0))
                    .ToList();
            var processedSilenceSegmentCount = renderSegments.Count(segment => segment.PlaybackSpeed > 1);

            if (cutPlan.RemovedSegments.Count == 0 && !useSilenceProcessing)
            {
                var passthroughOutputPath = _workspaceService.CreateOutputPath(video.FileName, useMp4Extension: false);
                File.Copy(uploadPath, passthroughOutputPath, overwrite: true);

                var passthroughMessage = explicitCutRanges.Count > 0
                    ? "No valid silence or manual/AI cut ranges were left after normalization, so the original file was copied as-is."
                    : automaticSilenceEnabled
                        ? "No silence matched the current thresholds, so the original file was copied as-is."
                        : "Automatic silence detection is off, so the original file was copied as-is.";

                return new VideoProcessingResult(
                    Success: true,
                    Message: passthroughMessage,
                    OriginalFileName: video.FileName,
                    OutputFileName: Path.GetFileName(passthroughOutputPath),
                    OutputPath: passthroughOutputPath,
                    OutputUrl: _workspaceService.GetOutputUrl(passthroughOutputPath),
                    SourceDurationSeconds: analysis.DurationSeconds,
                    OutputDurationSeconds: analysis.DurationSeconds,
                    RemovedDurationSeconds: 0,
                    RemovedSegmentsCount: 0,
                    CutsApplied: false);
            }

            if (useSilenceProcessing &&
                normalizedExplicitCutRanges.Count == 0 &&
                processedSilenceSegmentCount == 0)
            {
                var passthroughOutputPath = _workspaceService.CreateOutputPath(video.FileName, useMp4Extension: false);
                File.Copy(uploadPath, passthroughOutputPath, overwrite: true);

                return new VideoProcessingResult(
                    Success: true,
                    Message: automaticSilenceEnabled
                        ? "No silence matched the current thresholds, so the original file was copied as-is."
                        : "Automatic silence detection is off, so the original file was copied as-is.",
                    OriginalFileName: video.FileName,
                    OutputFileName: Path.GetFileName(passthroughOutputPath),
                    OutputPath: passthroughOutputPath,
                    OutputUrl: _workspaceService.GetOutputUrl(passthroughOutputPath),
                    SourceDurationSeconds: analysis.DurationSeconds,
                    OutputDurationSeconds: analysis.DurationSeconds,
                    RemovedDurationSeconds: 0,
                    RemovedSegmentsCount: 0,
                    CutsApplied: false);
            }

            if (renderSegments.Count == 0)
            {
                return BuildFailure(video.FileName, "The current cut and pause-speed settings leave no output to render. Reduce the cuts and try again.");
            }

            var outputPath = _workspaceService.CreateOutputPath(video.FileName, useMp4Extension: true);
            var appliedCrossfadeSeconds = await _ffmpegService.RenderWithoutSilenceAsync(
                uploadPath,
                outputPath,
                renderSegments,
                crossfadeMilliseconds,
                videoCrossfadeFrames,
                analysis.FrameRate,
                cancellationToken);

            var successMessage = BuildSuccessMessage(explicitCutRanges.Count, pauseSpeedMultiplier, retainedSilenceSeconds);

            var outputDurationSeconds = Math.Max(0, renderSegments.Sum(segment => segment.OutputDurationSeconds) - appliedCrossfadeSeconds);
            var removedDurationSeconds = Math.Max(0, cutPlan.SourceDurationSeconds - outputDurationSeconds);

            return new VideoProcessingResult(
                Success: true,
                Message: successMessage,
                OriginalFileName: video.FileName,
                OutputFileName: Path.GetFileName(outputPath),
                OutputPath: outputPath,
                OutputUrl: _workspaceService.GetOutputUrl(outputPath),
                SourceDurationSeconds: cutPlan.SourceDurationSeconds,
                OutputDurationSeconds: outputDurationSeconds,
                RemovedDurationSeconds: removedDurationSeconds,
                RemovedSegmentsCount: useSilenceProcessing
                    ? normalizedExplicitCutRanges.Count + processedSilenceSegmentCount
                    : cutPlan.RemovedSegments.Count,
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
            RemovedSegmentsCount: 0,
            CutsApplied: false);
    }

    private static string BuildSuccessMessage(
        int explicitCutCount,
        double pauseSpeedMultiplier,
        double retainedSilenceSeconds)
    {
        if (pauseSpeedMultiplier > 1 && retainedSilenceSeconds > 0)
        {
            return explicitCutCount > 0
                ? $"Video processed successfully with {pauseSpeedMultiplier:0.0}x pause compression, {retainedSilenceSeconds:0.##} s kept pauses, and manual/AI cut ranges."
                : $"Video processed successfully with {pauseSpeedMultiplier:0.0}x pause compression and {retainedSilenceSeconds:0.##} s kept pauses.";
        }

        if (pauseSpeedMultiplier > 1)
        {
            return explicitCutCount > 0
                ? $"Video processed successfully with {pauseSpeedMultiplier:0.0}x pause compression and manual/AI cut ranges."
                : $"Video processed successfully with {pauseSpeedMultiplier:0.0}x pause compression.";
        }

        if (retainedSilenceSeconds > 0)
        {
            return explicitCutCount > 0
                ? $"Video processed successfully with {retainedSilenceSeconds:0.##} s kept pauses and manual/AI cut ranges."
                : $"Video processed successfully with {retainedSilenceSeconds:0.##} s kept pauses.";
        }

        return explicitCutCount > 0
            ? "Video processed successfully with manual/AI cut ranges."
            : "Video processed successfully.";
    }
}
