namespace Strippr.Models;

public sealed record SileroVadAnalysisResult(
    double DurationSeconds,
    int SampleRate,
    int SampleCount,
    IReadOnlyList<SpeechInterval> SpeechIntervals,
    IReadOnlyList<SilenceInterval> SilenceIntervals,
    string Message);
