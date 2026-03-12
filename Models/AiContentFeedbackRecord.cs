namespace Strippr.Models;

public sealed record AiContentFeedbackRecord(
    DateTime CreatedUtc,
    string SessionId,
    string ClientFileName,
    string SpokenLanguage,
    string TranscriptionModel,
    string AnalysisModel,
    string TranscriptText,
    IReadOnlyList<AiTranscriptSegment> TranscriptSegments,
    AiContentFeedbackSuggestion Suggestion);
