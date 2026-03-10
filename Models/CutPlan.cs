namespace Strippr.Models;

public sealed record CutPlan(
    double SourceDurationSeconds,
    IReadOnlyList<SilenceInterval> RemovedSegments,
    IReadOnlyList<KeepSegment> KeepSegments)
{
    public double OutputDurationSeconds => KeepSegments.Sum(segment => segment.DurationSeconds);

    public double RemovedDurationSeconds => RemovedSegments.Sum(segment => segment.DurationSeconds);
}
