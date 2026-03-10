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

    public IReadOnlyList<RenderSegment> BuildRenderSegments(
        double sourceDurationSeconds,
        IReadOnlyList<SilenceInterval> silenceIntervals,
        IReadOnlyList<SilenceInterval> manualCutRanges,
        double minimumKeepSegmentSeconds,
        double pauseSpeedMultiplier,
        double retainedSilenceSeconds)
    {
        var normalizedSilences = Normalize(sourceDurationSeconds, silenceIntervals);
        var normalizedManualCuts = Normalize(sourceDurationSeconds, manualCutRanges);
        var compressibleSilences = Subtract(normalizedSilences, normalizedManualCuts);
        var timelineEvents = normalizedManualCuts
            .Select(interval => new TimelineEvent(interval.StartSeconds, interval.EndSeconds, IsHardCut: true))
            .Concat(compressibleSilences.Select(interval => new TimelineEvent(interval.StartSeconds, interval.EndSeconds, IsHardCut: false)))
            .OrderBy(interval => interval.StartSeconds)
            .ThenBy(interval => interval.EndSeconds)
            .ToList();

        var renderSegments = new List<RenderSegment>();
        var cursor = 0d;
        var nextSegmentStartsAfterHardCut = false;

        foreach (var timelineEvent in timelineEvents)
        {
            AddRenderSegment(
                renderSegments,
                cursor,
                timelineEvent.StartSeconds,
                playbackSpeed: 1,
                nextSegmentStartsAfterHardCut,
                minimumKeepSegmentSeconds,
                applyMinimumKeepThreshold: true);
            nextSegmentStartsAfterHardCut = false;

            if (timelineEvent.IsHardCut)
            {
                cursor = timelineEvent.EndSeconds;
                nextSegmentStartsAfterHardCut = true;
                continue;
            }

            AddRenderSegment(
                renderSegments,
                timelineEvent.StartSeconds,
                timelineEvent.EndSeconds,
                CalculateSilencePlaybackSpeed(timelineEvent.EndSeconds - timelineEvent.StartSeconds, pauseSpeedMultiplier, retainedSilenceSeconds),
                nextSegmentStartsAfterHardCut,
                minimumKeepSegmentSeconds,
                applyMinimumKeepThreshold: false);

            cursor = timelineEvent.EndSeconds;
            nextSegmentStartsAfterHardCut = false;
        }

        AddRenderSegment(
            renderSegments,
            cursor,
            sourceDurationSeconds,
            playbackSpeed: 1,
            nextSegmentStartsAfterHardCut,
            minimumKeepSegmentSeconds,
            applyMinimumKeepThreshold: true);

        return renderSegments;
    }

    public IReadOnlyList<SilenceInterval> Normalize(
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

    public IReadOnlyList<SilenceInterval> ApplyCutHandles(
        IReadOnlyList<SilenceInterval> silenceIntervals,
        double handleSeconds)
    {
        if (silenceIntervals.Count == 0 || handleSeconds <= 0)
        {
            return silenceIntervals.ToList();
        }

        return silenceIntervals
            .Select(interval => new SilenceInterval(
                interval.StartSeconds + handleSeconds,
                interval.EndSeconds - handleSeconds))
            .Where(interval => interval.EndSeconds > interval.StartSeconds)
            .ToList();
    }

    public IReadOnlyList<SilenceInterval> Subtract(
        IReadOnlyList<SilenceInterval> sourceIntervals,
        IReadOnlyList<SilenceInterval> excludedIntervals)
    {
        if (sourceIntervals.Count == 0)
        {
            return [];
        }

        if (excludedIntervals.Count == 0)
        {
            return sourceIntervals.ToList();
        }

        var result = new List<SilenceInterval>();
        var excludedIndex = 0;

        foreach (var sourceInterval in sourceIntervals)
        {
            var cursor = sourceInterval.StartSeconds;

            while (excludedIndex < excludedIntervals.Count &&
                   excludedIntervals[excludedIndex].EndSeconds <= sourceInterval.StartSeconds)
            {
                excludedIndex++;
            }

            var currentExcludedIndex = excludedIndex;
            while (currentExcludedIndex < excludedIntervals.Count &&
                   excludedIntervals[currentExcludedIndex].StartSeconds < sourceInterval.EndSeconds)
            {
                var excluded = excludedIntervals[currentExcludedIndex];
                if (excluded.StartSeconds > cursor)
                {
                    result.Add(new SilenceInterval(cursor, Math.Min(excluded.StartSeconds, sourceInterval.EndSeconds)));
                }

                cursor = Math.Max(cursor, excluded.EndSeconds);
                if (cursor >= sourceInterval.EndSeconds)
                {
                    break;
                }

                currentExcludedIndex++;
            }

            if (cursor < sourceInterval.EndSeconds)
            {
                result.Add(new SilenceInterval(cursor, sourceInterval.EndSeconds));
            }
        }

        return result;
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

    private static void AddRenderSegment(
        ICollection<RenderSegment> renderSegments,
        double startSeconds,
        double endSeconds,
        double playbackSpeed,
        bool startsAfterHardCut,
        double minimumKeepSegmentSeconds,
        bool applyMinimumKeepThreshold)
    {
        var durationSeconds = endSeconds - startSeconds;
        if (durationSeconds <= 0)
        {
            return;
        }

        if (applyMinimumKeepThreshold && durationSeconds < minimumKeepSegmentSeconds)
        {
            return;
        }

        renderSegments.Add(new RenderSegment(
            startSeconds,
            endSeconds,
            Math.Max(1, playbackSpeed),
            startsAfterHardCut && renderSegments.Count > 0));
    }

    private static double CalculateSilencePlaybackSpeed(
        double durationSeconds,
        double pauseSpeedMultiplier,
        double retainedSilenceSeconds)
    {
        if (durationSeconds <= 0)
        {
            return 1;
        }

        double targetDurationSeconds;
        if (retainedSilenceSeconds > 0 && pauseSpeedMultiplier <= 1)
        {
            targetDurationSeconds = Math.Min(durationSeconds, retainedSilenceSeconds);
        }
        else
        {
            targetDurationSeconds = durationSeconds / Math.Max(1, pauseSpeedMultiplier);
            if (retainedSilenceSeconds > 0)
            {
                targetDurationSeconds = Math.Max(retainedSilenceSeconds, targetDurationSeconds);
            }
        }

        targetDurationSeconds = Math.Clamp(targetDurationSeconds, 0, durationSeconds);
        return targetDurationSeconds <= 0 ? 1 : Math.Max(1, durationSeconds / targetDurationSeconds);
    }

    private sealed record TimelineEvent(double StartSeconds, double EndSeconds, bool IsHardCut);
}
