namespace Strippr.Models;

public sealed record AiContentFeedbackSuggestion(
    string SuggestionId,
    string Action,
    string? Label,
    string? CorrectedLabel,
    string? Reason,
    string? Excerpt,
    double Confidence,
    double OriginalStartSeconds,
    double OriginalEndSeconds,
    IReadOnlyList<AiContentFeedbackRange>? CurrentRanges,
    string? Note);
