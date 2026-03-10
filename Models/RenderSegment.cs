namespace Strippr.Models;

public sealed record RenderSegment(
    double StartSeconds,
    double EndSeconds,
    double PlaybackSpeed,
    bool StartsAfterHardCut)
{
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);

    public double OutputDurationSeconds => PlaybackSpeed > 0
        ? DurationSeconds / PlaybackSpeed
        : 0;
}
