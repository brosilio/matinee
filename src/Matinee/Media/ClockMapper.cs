using Httpgrl;

namespace Matinee.Media;

/// <summary>
/// Maps a source's timestamps (milliseconds on the sender's clock) onto the server clock, absorbing network jitter.
/// Used for RTMP ingest (OBS → us) and for an uplink (home instance → cloud instance).
///
/// A frame's stamp is its source time plus an offset, shared by every track of the source (which keeps them in sync).
/// The offset follows the worst lateness of the last <see cref="WindowMs"/>:
/// <list type="bullet">
/// <item>A frame that arrives later than its stamp would be raises the offset at once: frames are never stamped
/// in the past, which would eat the viewers' delay budget.</item>
/// <item>When the offset is more than <see cref="SkipMs"/> above what the recent frames needed (an upload hiccup or
/// encoder warm-up has passed), it drops straight back. Stamps must never go backwards, so the caller drops frames
/// that would land behind ones already sent (see <see cref="Behind"/>): viewers play through what they have
/// buffered, skip that much content, and are back at the target latency within seconds rather than minutes.</item>
/// <item>Smaller excess decays slowly, without dropping anything.</item>
/// <item>A frame more than <see cref="ReanchorMs"/> earlier than the offset expects is a discontinuity (source
/// timestamps jumped), not jitter: re-anchor on it.</item>
/// </list>
/// </summary>
public sealed class ClockMapper(ILogger log, string what, Func<double>? clock = null)
{
    internal const double WindowMs = 4000;
    internal const double SkipMs = 250;
    internal const double ReanchorMs = 2000;
    private const double DecayPerFrameMs = 0.1;

    private readonly Func<double> _now = clock ?? (static () => Clock.NowMs);
    private double _offset = double.NaN;

    // Sliding-window maximum of (arrival - source time): a monotonic deque in a ring.
    private readonly double[] _winT = new double[2048], _winRaw = new double[2048];
    private int _winHead, _winCount;

    /// <summary>Map a frame that drives the timing (it updates the offset).</summary>
    public double Map(double srcMs)
    {
        double now = _now();
        double raw = now - srcMs;
        if (!double.IsNaN(_offset) && _offset - raw > ReanchorMs)
        {
            log.Info($"{what}: source timestamps jumped; re-anchoring", new LogField("jump_ms", Math.Round(_offset - raw)));
            _winCount = 0;
            _offset = double.NaN;
        }
        double target = PushWindow(now, raw);
        // The window maximum includes this frame, so target >= raw: raising to it means "never stamp in the past".
        if (double.IsNaN(_offset) || target > _offset) _offset = target;
        else if (_offset - target > SkipMs)
        {
            log.Info($"{what}: cutting latency", new LogField("ms", Math.Round(_offset - target)));
            _offset = target;
        }
        else _offset = Math.Max(target, _offset - DecayPerFrameMs);
        return srcMs + _offset;
    }

    /// <summary>
    /// Map a frame with the current offset, without letting it move: for streams that ride on another one's timing
    /// (an uplink's re-encodes follow its source, whatever their own encoding delay).
    /// </summary>
    public double MapFollowing(double srcMs) => double.IsNaN(_offset) ? Map(srcMs) : srcMs + _offset;

    private double PushWindow(double t, double raw)
    {
        int cap = _winT.Length;
        // Drop samples that left the window (front), and samples the new one dominates (back).
        while (_winCount > 0 && t - _winT[_winHead] > WindowMs) { _winHead = (_winHead + 1) % cap; _winCount--; }
        while (_winCount > 0 && _winRaw[(_winHead + _winCount - 1) % cap] <= raw) _winCount--;
        if (_winCount == cap) { _winHead = (_winHead + 1) % cap; _winCount--; }
        int i = (_winHead + _winCount) % cap;
        _winT[i] = t;
        _winRaw[i] = raw;
        _winCount++;
        return _winRaw[_winHead];
    }

    /// <summary>
    /// Stamps must increase per track. A stamp at or behind the last one means the offset was just lowered: drop the
    /// frame (true). Sub-millisecond ties (duplicate source timestamps) are nudged instead.
    /// </summary>
    public static bool Behind(ref double stamp, double last)
    {
        if (stamp > last) return false;
        if (last - stamp < 1) { stamp = last + 0.001; return false; }
        return true;
    }
}
