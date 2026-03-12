namespace Strippr.Models;

public sealed record SpeechInterval(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
}
