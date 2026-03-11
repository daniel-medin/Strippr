namespace Strippr.Models;

public sealed record AiTranscriptSegment(
    double StartSeconds,
    double EndSeconds,
    string Text);
