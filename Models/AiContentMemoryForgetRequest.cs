namespace Strippr.Models;

public sealed record AiContentMemoryForgetRequest(
    string ClientFileName,
    string? TranscriptText);
