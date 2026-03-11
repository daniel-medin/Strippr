namespace Strippr.Models;

public sealed record AiContentAnalysisResult(
    string TranscriptionModel,
    string AnalysisModel,
    double DurationSeconds,
    string TranscriptText,
    string Summary,
    IReadOnlyList<AiTranscriptSegment> TranscriptSegments,
    IReadOnlyList<AiContentIssue> Issues,
    string Message);
