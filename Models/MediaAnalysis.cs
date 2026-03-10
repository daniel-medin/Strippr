namespace Strippr.Models;

public sealed record MediaAnalysis(
    double DurationSeconds,
    double FrameRate,
    IReadOnlyList<SilenceInterval> SilenceIntervals);
