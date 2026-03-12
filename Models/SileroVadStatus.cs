namespace Strippr.Models;

public sealed record SileroVadStatus(
    bool Enabled,
    bool RuntimeAvailable,
    bool ModelFileExists,
    string ModelPath,
    int SampleRate,
    float SpeechThreshold,
    float NegativeSpeechThreshold,
    int MinSpeechMilliseconds,
    int MinSilenceMilliseconds,
    int SpeechPadMilliseconds,
    string Message);
