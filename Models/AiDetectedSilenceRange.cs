namespace Strippr.Models;

public sealed record AiDetectedSilenceRange(
    double StartSeconds,
    double EndSeconds);
