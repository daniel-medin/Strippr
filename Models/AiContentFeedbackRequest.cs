namespace Strippr.Models;

public sealed record AiContentFeedbackRequest(
    string SessionId,
    string ClientFileName,
    string? SpokenLanguage,
    string? TranscriptionModel,
    string? AnalysisModel,
    string? TranscriptText,
    IReadOnlyList<AiTranscriptSegment>? TranscriptSegments,
    AiContentFeedbackSuggestion? Suggestion);
