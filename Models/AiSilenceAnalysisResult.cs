namespace Strippr.Models;

public sealed record AiSilenceAnalysisResult(
    string Model,
    double DurationSeconds,
    IReadOnlyList<AiDetectedSilenceRange> SilenceRanges,
    string Message);
