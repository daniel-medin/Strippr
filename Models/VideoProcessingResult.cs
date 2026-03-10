namespace Strippr.Models;

public sealed record VideoProcessingResult(
    bool Success,
    string Message,
    string OriginalFileName,
    string? OutputFileName,
    string? OutputPath,
    string? OutputUrl,
    double SourceDurationSeconds,
    double OutputDurationSeconds,
    double RemovedDurationSeconds,
    int RemovedSegmentsCount,
    bool CutsApplied);
