namespace Matinee.Room;

internal enum SyncEventKind : byte { None, Pause, Resume, DropViewer, CaughtUp }

internal readonly record struct SyncEvent(
    SyncEventKind Kind,
    int ViewerId = 0,
    string? Nick = null,
    double PauseBudgetMs = 0,
    bool TimelineChanged = false);

/// <summary>
/// The shared sync-mode playhead. A viewer below <see cref="LowBufferMs"/> pauses the room, and playback resumes
/// once that viewer has <see cref="ReadyBufferMs"/>. Time spent paused is recovered at 1.05x. The configured
/// maximum delay is a hard ceiling: a viewer that cannot recover within it is reconnected at the current playhead
/// instead of repeatedly holding the whole room.
/// </summary>
internal sealed class SyncTimeline
{
    internal const double LowBufferMs = 500;
    internal const double ReadyBufferMs = 2000;
    internal const double ReportFreshMs = 1500;
    private const double CatchUpRate = 1.05;

    private double _pauseDeadline;
    private string? _pausedNick;

    public double Wall { get; private set; }
    public double Media { get; private set; }
    public double Rate { get; private set; } = 1;
    public int PausedViewerId { get; private set; }

    public void Reset(double now, int delayMs)
    {
        Wall = now;
        Media = now - delayMs;
        Rate = 1;
        PausedViewerId = 0;
        _pausedNick = null;
        _pauseDeadline = 0;
    }

    public double Playhead(double now) => Media + (now - Wall) * Rate;

    /// <param name="targetPresent">Whether the viewer that caused the current pause is still in the room.</param>
    /// <param name="targetFresh">Whether its buffer report is recent enough to act on.</param>
    /// <param name="slowViewerId">The slowest recently-reporting viewer below the low-buffer threshold.</param>
    public SyncEvent Tick(
        double now,
        int delayMs,
        int maxDelayMs,
        bool targetPresent,
        bool targetFresh,
        double targetAheadMs,
        int slowViewerId,
        string? slowNick)
    {
        if (Rate == 0)
        {
            if (!targetPresent || (targetFresh && targetAheadMs >= ReadyBufferMs))
            {
                int id = PausedViewerId;
                string? nick = _pausedNick;
                Resume(now);
                return new(SyncEventKind.Resume, id, nick, TimelineChanged: true);
            }
            if (now >= _pauseDeadline)
            {
                int id = PausedViewerId;
                string? nick = _pausedNick;
                Resume(now);
                return new(SyncEventKind.DropViewer, id, nick, TimelineChanged: true);
            }
            return default;
        }

        if (Rate > 1 && Playhead(now) >= now - delayMs)
        {
            Wall = now;
            Media = now - delayMs;
            Rate = 1;
            return new(SyncEventKind.CaughtUp, TimelineChanged: true);
        }

        if (slowViewerId == 0) return default;

        double budget = maxDelayMs - (now - Playhead(now));
        if (budget <= 0)
            return new(SyncEventKind.DropViewer, slowViewerId, slowNick);

        SetRate(now, 0);
        PausedViewerId = slowViewerId;
        _pausedNick = slowNick;
        _pauseDeadline = now + budget;
        return new(SyncEventKind.Pause, slowViewerId, slowNick, budget, TimelineChanged: true);
    }

    private void Resume(double now)
    {
        SetRate(now, CatchUpRate);
        PausedViewerId = 0;
        _pausedNick = null;
        _pauseDeadline = 0;
    }

    private void SetRate(double now, double rate)
    {
        Media = Playhead(now);
        Wall = now;
        Rate = rate;
    }
}
