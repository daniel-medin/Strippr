namespace Strippr.Models;

public sealed record SileroVadComparisonResult(
    double DurationSeconds,
    string FfmpegNoiseThreshold,
    double FfmpegMinimumSilenceSeconds,
    SileroVadAnalysisResult Silero,
    IReadOnlyList<SilenceInterval> FfmpegSilenceIntervals,
    string Message);
