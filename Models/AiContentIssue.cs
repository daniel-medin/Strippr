namespace Strippr.Models;

public sealed record AiContentIssue(
    double StartSeconds,
    double EndSeconds,
    string Label,
    string Reason,
    string Excerpt,
    double Confidence,
    bool IsLearned = false);
