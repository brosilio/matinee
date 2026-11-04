using System.Diagnostics;

namespace Matinee.Media;

/// <summary>
/// The server's timeline: milliseconds on a monotonic clock since process start. Every media timestamp sent to
/// viewers is on this clock, and viewers sync to it, so every client agrees on what "now" is.
/// </summary>
public static class Clock
{
    private static readonly long Epoch = Stopwatch.GetTimestamp();
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    public static double NowMs => (Stopwatch.GetTimestamp() - Epoch) * MsPerTick;
}
