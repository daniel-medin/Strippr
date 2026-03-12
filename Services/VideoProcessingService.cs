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
    private readonly SileroVadService _sileroVadService;
    private readonly WorkspaceService _workspaceService;

    public VideoProcessingService(
        CutPlanBuilder cutPlanBuilder,
        FfmpegService ffmpegService,
        ILogger<VideoProcessingService> logger,
        IOptions<StripprOptions> options,
        SileroVadService sileroVadService,
        WorkspaceService workspaceService)
    {
        _cutPlanBuilder = cutPlanBuilder;
        _ffmpegService = ffmpegService;
        _logger = logger;
        _options = options.Value;
        _sileroVadService = sileroVadService;
        _workspaceService = workspaceService;
    }

    public async Task<VideoProcessingResult> ProcessAsync(
        IFormFile video,
        bool automaticSilenceEnabled,
        string automaticSilenceAnalyzer,
        string noiseThreshold,
        double minimumSilenceSeconds,
        double retainedSilenceSeconds,
        double cutHandleMilliseconds,
        double crossfadeMilliseconds,
        int videoCrossfadeFrames,
        double pauseSpeedMultiplier,
        IReadOnlyList<SilenceInterval> automaticCutRanges,
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
                AutomaticSilenceAnalyzerUsed: "ffmpeg",
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

        var analyzerUsed = automaticSilenceEnabled ? NormalizeAnalyzerMode(automaticSilenceAnalyzer) : "off";

        try
        {
            var analysis = automaticSilenceEnabled
                ? await AnalyzeAutomaticSilenceAsync(
                    uploadPath,
                    automaticSilenceAnalyzer,
                    noiseThreshold,
                    minimumSilenceSeconds,
                    cancellationToken)
                : new AutomaticSilenceAnalysisResult(
                    await _ffmpegService.AnalyzeMediaAsync(
                        uploadPath,
                        cancellationToken),
                    AnalyzerUsed: "off");
            analyzerUsed = analysis.AnalyzerUsed;

            var normalizedExplicitCutRanges = _cutPlanBuilder.Normalize(analysis.Media.DurationSeconds, explicitCutRanges);
            var normalizedSilenceRanges = _cutPlanBuilder.Normalize(analysis.Media.DurationSeconds, analysis.Media.SilenceIntervals);
            var cutHandleSeconds = Math.Max(0, cutHandleMilliseconds) / 1000d;
            var handledSilenceRanges = automaticSilenceEnabled && automaticCutRanges.Count > 0
                ? _cutPlanBuilder.Normalize(analysis.Media.DurationSeconds, automaticCutRanges)
                : _cutPlanBuilder.ApplyCutHandles(normalizedSilenceRanges, cutHandleSeconds);
            var useSilenceProcessing = pauseSpeedMultiplier > 1 || retainedSilenceSeconds > 0;
            var cutPlan = useSilenceProcessing
                ? _cutPlanBuilder.Build(
                    analysis.Media.DurationSeconds,
                    normalizedExplicitCutRanges,
                    _options.MinimumKeepSegmentSeconds)
                : _cutPlanBuilder.Build(
                    analysis.Media.DurationSeconds,
                    handledSilenceRanges.Concat(normalizedExplicitCutRanges).ToList(),
                    _options.MinimumKeepSegmentSeconds);
            var renderSegments = useSilenceProcessing
                ? _cutPlanBuilder.BuildRenderSegments(
                    analysis.Media.DurationSeconds,
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
                    AutomaticSilenceAnalyzerUsed: analyzerUsed,
                    OutputFileName: Path.GetFileName(passthroughOutputPath),
                    OutputPath: passthroughOutputPath,
                    OutputUrl: _workspaceService.GetOutputUrl(passthroughOutputPath),
                    SourceDurationSeconds: analysis.Media.DurationSeconds,
                    OutputDurationSeconds: analysis.Media.DurationSeconds,
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
                    AutomaticSilenceAnalyzerUsed: analyzerUsed,
                    OutputFileName: Path.GetFileName(passthroughOutputPath),
                    OutputPath: passthroughOutputPath,
                    OutputUrl: _workspaceService.GetOutputUrl(passthroughOutputPath),
                    SourceDurationSeconds: analysis.Media.DurationSeconds,
                    OutputDurationSeconds: analysis.Media.DurationSeconds,
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
                analysis.Media.FrameRate,
                cancellationToken);

            var successMessage = BuildSuccessMessage(
                explicitCutRanges.Count,
                pauseSpeedMultiplier,
                retainedSilenceSeconds,
                analyzerUsed);

            var outputDurationSeconds = Math.Max(0, renderSegments.Sum(segment => segment.OutputDurationSeconds) - appliedCrossfadeSeconds);
            var removedDurationSeconds = Math.Max(0, cutPlan.SourceDurationSeconds - outputDurationSeconds);

            return new VideoProcessingResult(
                Success: true,
                Message: successMessage,
                OriginalFileName: video.FileName,
                AutomaticSilenceAnalyzerUsed: analyzerUsed,
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
            return BuildFailure(video.FileName, exception.Message, analyzerUsed);
        }
    }

    private static VideoProcessingResult BuildFailure(string originalFileName, string message, string automaticSilenceAnalyzerUsed = "ffmpeg")
    {
        return new VideoProcessingResult(
            Success: false,
            Message: message,
            OriginalFileName: originalFileName,
            AutomaticSilenceAnalyzerUsed: automaticSilenceAnalyzerUsed,
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
        double retainedSilenceSeconds,
        string analyzerUsed)
    {
        var analyzerSuffix = analyzerUsed switch
        {
            "silero" => " Auto silence: Silero VAD.",
            "hybrid" => " Auto silence: Hybrid.",
            "ffmpeg-fallback" => " Auto silence: FFmpeg fallback.",
            "off" => " Auto silence: Off.",
            _ => " Auto silence: FFmpeg."
        };

        if (pauseSpeedMultiplier > 1 && retainedSilenceSeconds > 0)
        {
            var message = explicitCutCount > 0
                ? $"Video processed successfully with {pauseSpeedMultiplier:0.0}x pause compression, {retainedSilenceSeconds:0.##} s kept pauses, and manual/AI cut ranges."
                : $"Video processed successfully with {pauseSpeedMultiplier:0.0}x pause compression and {retainedSilenceSeconds:0.##} s kept pauses.";
            return message + analyzerSuffix;
        }

        if (pauseSpeedMultiplier > 1)
        {
            var message = explicitCutCount > 0
                ? $"Video processed successfully with {pauseSpeedMultiplier:0.0}x pause compression and manual/AI cut ranges."
                : $"Video processed successfully with {pauseSpeedMultiplier:0.0}x pause compression.";
            return message + analyzerSuffix;
        }

        if (retainedSilenceSeconds > 0)
        {
            var message = explicitCutCount > 0
                ? $"Video processed successfully with {retainedSilenceSeconds:0.##} s kept pauses and manual/AI cut ranges."
                : $"Video processed successfully with {retainedSilenceSeconds:0.##} s kept pauses.";
            return message + analyzerSuffix;
        }

        var defaultMessage = explicitCutCount > 0
            ? "Video processed successfully with manual/AI cut ranges."
            : "Video processed successfully.";
        return defaultMessage + analyzerSuffix;
    }

    private async Task<AutomaticSilenceAnalysisResult> AnalyzeAutomaticSilenceAsync(
        string uploadPath,
        string automaticSilenceAnalyzer,
        string noiseThreshold,
        double minimumSilenceSeconds,
        CancellationToken cancellationToken)
    {
        var mode = NormalizeAnalyzerMode(automaticSilenceAnalyzer);
        if (string.Equals(mode, "ffmpeg", StringComparison.Ordinal))
        {
            return new AutomaticSilenceAnalysisResult(
                await _ffmpegService.AnalyzeSilenceAsync(
                    uploadPath,
                    noiseThreshold,
                    minimumSilenceSeconds,
                    cancellationToken),
                AnalyzerUsed: "ffmpeg");
        }

        try
        {
            var mediaAnalysis = await _ffmpegService.AnalyzeMediaAsync(uploadPath, cancellationToken);

            if (string.Equals(mode, "silero", StringComparison.Ordinal))
            {
                var sileroAnalysis = await _sileroVadService.AnalyzeFileAsync(uploadPath, cancellationToken);
                return new AutomaticSilenceAnalysisResult(
                    new MediaAnalysis(
                        mediaAnalysis.DurationSeconds,
                        mediaAnalysis.FrameRate,
                        sileroAnalysis.SilenceIntervals),
                    AnalyzerUsed: "silero");
            }

            if (string.Equals(mode, "hybrid", StringComparison.Ordinal))
            {
                var comparison = await _sileroVadService.CompareFileAsync(
                    uploadPath,
                    cancellationToken,
                    noiseThreshold,
                    minimumSilenceSeconds);

                var hybridIntervals = BuildHybridSilenceIntervals(
                    comparison.FfmpegSilenceIntervals,
                    comparison.Silero.SilenceIntervals);

                return new AutomaticSilenceAnalysisResult(
                    new MediaAnalysis(
                        mediaAnalysis.DurationSeconds,
                        mediaAnalysis.FrameRate,
                        hybridIntervals),
                    AnalyzerUsed: "hybrid");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Automatic silence analyzer {Analyzer} failed. Falling back to FFmpeg silencedetect.",
                automaticSilenceAnalyzer);
        }

        return new AutomaticSilenceAnalysisResult(
            await _ffmpegService.AnalyzeSilenceAsync(
                uploadPath,
                noiseThreshold,
                minimumSilenceSeconds,
                cancellationToken),
            AnalyzerUsed: "ffmpeg-fallback");
    }

    private static string NormalizeAnalyzerMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "silero" => "silero",
            "hybrid" => "hybrid",
            _ => "ffmpeg"
        };
    }

    private static IReadOnlyList<SilenceInterval> BuildHybridSilenceIntervals(
        IReadOnlyList<SilenceInterval> ffmpegIntervals,
        IReadOnlyList<SilenceInterval> sileroIntervals)
    {
        var hybridIntervals = new List<SilenceInterval>();

        foreach (var ffmpegInterval in ffmpegIntervals)
        {
            foreach (var sileroInterval in sileroIntervals)
            {
                var overlapStart = Math.Max(ffmpegInterval.StartSeconds, sileroInterval.StartSeconds);
                var overlapEnd = Math.Min(ffmpegInterval.EndSeconds, sileroInterval.EndSeconds);
                if (overlapEnd - overlapStart <= 0.08)
                {
                    continue;
                }

                hybridIntervals.Add(new SilenceInterval(overlapStart, overlapEnd));
            }
        }

        return hybridIntervals
            .OrderBy(interval => interval.StartSeconds)
            .ThenBy(interval => interval.EndSeconds)
            .ToList();
    }

    private sealed record AutomaticSilenceAnalysisResult(
        MediaAnalysis Media,
        string AnalyzerUsed);
}
