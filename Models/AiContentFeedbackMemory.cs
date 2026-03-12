namespace Strippr.Models;

public sealed record AiContentFeedbackMemory(
    IReadOnlyList<AiContentIssue> LearnedIssues,
    IReadOnlyList<AiContentFeedbackRange> SuppressedRanges,
    int MatchedFeedbackCount);
