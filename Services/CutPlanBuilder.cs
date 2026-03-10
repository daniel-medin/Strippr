using Strippr.Models;

namespace Strippr.Services;

public sealed class CutPlanBuilder
{
    public CutPlan Build(
        double sourceDurationSeconds,
        IReadOnlyList<SilenceInterval> silenceIntervals,
        double minimumKeepSegmentSeconds)
    {
        var normalizedSilences = Normalize(sourceDurationSeconds, silenceIntervals);
        var keepSegments = new List<KeepSegment>();
        var cursor = 0d;

        foreach (var silence in normalizedSilences)
        {
            AddKeepSegment(keepSegments, cursor, silence.StartSeconds, minimumKeepSegmentSeconds);
            cursor = silence.EndSeconds;
        }

        AddKeepSegment(keepSegments, cursor, sourceDurationSeconds, minimumKeepSegmentSeconds);

        return new CutPlan(sourceDurationSeconds, normalizedSilences, keepSegments);
    }

    private static List<SilenceInterval> Normalize(
        double sourceDurationSeconds,
        IReadOnlyList<SilenceInterval> silenceIntervals)
    {
        if (silenceIntervals.Count == 0)
        {
            return [];
        }

        var ordered = silenceIntervals
            .Select(interval => new SilenceInterval(
                Math.Clamp(interval.StartSeconds, 0, sourceDurationSeconds),
                Math.Clamp(interval.EndSeconds, 0, sourceDurationSeconds)))
            .Where(interval => interval.EndSeconds > interval.StartSeconds)
            .OrderBy(interval => interval.StartSeconds)
            .ToList();

        if (ordered.Count == 0)
        {
            return [];
        }

        var merged = new List<SilenceInterval> { ordered[0] };

        for (var index = 1; index < ordered.Count; index++)
        {
            var current = ordered[index];
            var previous = merged[^1];

            if (current.StartSeconds <= previous.EndSeconds)
            {
                merged[^1] = previous with
                {
                    EndSeconds = Math.Max(previous.EndSeconds, current.EndSeconds)
                };
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static void AddKeepSegment(
        ICollection<KeepSegment> keepSegments,
        double startSeconds,
        double endSeconds,
        double minimumKeepSegmentSeconds)
    {
        var durationSeconds = endSeconds - startSeconds;

        if (durationSeconds < minimumKeepSegmentSeconds)
        {
            return;
        }

        keepSegments.Add(new KeepSegment(startSeconds, endSeconds));
    }
}
