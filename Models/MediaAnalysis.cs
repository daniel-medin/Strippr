namespace Strippr.Models;

public sealed record MediaAnalysis(
    double DurationSeconds,
    IReadOnlyList<SilenceInterval> SilenceIntervals);
