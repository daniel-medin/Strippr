namespace Strippr.Models;

public sealed record AiAutoCutAnalysisResult(
    string TranscriptionModel,
    string AnalysisModel,
    double DurationSeconds,
    string TranscriptText,
    string TargetTranscript,
    string ResultTranscript,
    string Summary,
    IReadOnlyList<AiTranscriptSegment> TranscriptSegments,
    IReadOnlyList<AiContentIssue> Issues,
    string Message);
