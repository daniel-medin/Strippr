namespace Strippr.Models;

public sealed record SilenceInterval(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
}
