using Httpgrl;
using Matinee.Media;

namespace Matinee.Room;

/// <summary>What the adaptation policy needs to know about (and do to) a viewer.</summary>
internal interface IAdaptViewer
{
    int Id { get; }
    string Nick { get; }
    Rendition Rendition { get; }
    Rendition? PendingRendition { get; }
    long VideoSent { get; }
    long VideoDropped { get; }
    long AckedBytes { get; }
    void SwitchTo(Rendition r);
}

/// <summary>
/// Automatic low-bitrate renditions: re-encodes started for whoever in the room right now can't keep up, sized for
/// them.
///
/// <list type="bullet">
/// <item><b>Who's struggling.</b> Every viewer is measured over a window: the share of video frames the congestion
/// policy had to drop, and the throughput the client has acknowledged. Dropping at least
/// <see cref="AdaptSettings.DropThreshold"/> marks a viewer as struggling, and its acknowledged throughput while
/// struggling is its capacity (the link is saturated then, so that's what it can take). In sync mode, causing a room
/// pause counts as struggling too.</item>
/// <item><b>Groups.</b> Struggling viewers are grouped: with one transcoder (the default) they all share it; with
/// more, viewers within a factor of two of each other share one. A group's bitrate comes from its slowest member:
/// capacity × <see cref="AdaptSettings.Headroom"/>, less the audio, never above 80% of the source.</item>
/// <item><b>Retuning.</b> When a group's target drifts more than 30% from its transcoder's (someone slower joins,
/// the slowest one leaves or recovers), a new encoder starts; viewers move once it has a keyframe, then the old one
/// stops. Make before break: nobody sits on a dead stream.</item>
/// <item><b>Recovering.</b> A viewer that watches its re-encode without a single drop for its backoff time (30 s,
/// doubling up to 5 min after each failed attempt) steps up: its estimate rises to fit a re-encode about
/// <see cref="StepUp"/>× bigger, which retunes its group. Once its re-encode is as big as re-encodes get (80% of the
/// source), the next step is the source itself. Climbing in steps means a link that can take 1 Mbit/s isn't thrown
/// at a 2.5 Mbit/s source to find out.</item>
/// <item><b>Budget.</b> With <see cref="AdaptSettings.BudgetKbps"/> (an uplinked home instance's upload), no
/// re-encode starts that would push the source plus all re-encodes past it.</item>
/// <item><b>Cleanup.</b> A re-encode nobody has watched for 10 s stops. If one fails, its viewers go back to the
/// source and a replacement starts on the next tick.</item>
/// </list>
///
/// All switches happen at keyframes on a shared timeline (see <see cref="Viewer"/>), so they're seamless apart from
/// a video hold of up to a keyframe interval. Not thread-safe: the room calls it under its lock, once a second.
/// </summary>
internal sealed class Adaptive(Rendition source, Func<int, Rendition> start, Action<Rendition> stop, ILogger log)
{
    internal const double MinDwellMs = 8000;
    internal const double InitialBackoffMs = 30_000, MaxBackoffMs = 300_000;
    internal const double ProbeConfirmMs = 30_000;
    internal const double RetuneMinAgeMs = 20_000, RetuneRatio = 0.3;
    internal const double IdleStopMs = 10_000;
    internal const double StepUp = 1.5;
    private const double StartRetryMs = 30_000;

    private sealed class Tier(Rendition r)
    {
        public readonly Rendition R = r;
        public Tier? ReplacedBy;
        public double IdleSince = double.NaN;
        public bool Dead => R.Failed;
    }

    private sealed class State
    {
        public readonly Queue<(double T, long Sent, long Dropped, long Acked)> Samples = new();
        public double Capacity = double.NaN;          // kbit/s, measured while struggling
        public double LastChange = double.NegativeInfinity;
        public double Backoff = InitialBackoffMs;
        public double ProbeAt = double.NaN;           // when we last stepped it up (or tried the source)
        public bool WantsTier;
        public bool Forced;                           // sync mode: caused a pause
    }

    private readonly List<Tier> _tiers = new();
    private readonly Dictionary<int, State> _states = new();
    private double _sourceKbps = double.NaN;
    private long _lastSourceBytes = -1;
    private double _lastTick = double.NaN;
    private double _noStartUntil = double.NegativeInfinity;
    private double _budgetLoggedAt = double.NegativeInfinity;

    public IEnumerable<Rendition> Renditions { get { foreach (var t in _tiers) yield return t.R; } }
    public double SourceKbps => _sourceKbps;

    /// <summary>Sync mode: this viewer held the room up. Treat it as struggling on the next tick.</summary>
    public void Struggled(int viewerId)
    {
        if (_states.TryGetValue(viewerId, out var st)) st.Forced = true;
    }

    public void Tick(double now, IReadOnlyList<IAdaptViewer> viewers, AdaptSettings s, long sourceBytes)
    {
        MeasureSource(now, sourceBytes);
        if (!s.Enabled)
        {
            StopAll(viewers);
            return;
        }

        // Forget viewers who left.
        if (_states.Count > viewers.Count)
        {
            var present = new HashSet<int>();
            foreach (var v in viewers) present.Add(v.Id);
            foreach (var id in _states.Keys.ToArray()) if (!present.Contains(id)) _states.Remove(id);
        }

        foreach (var v in viewers) Classify(now, v, s);
        var groups = Groups(viewers, s);
        var dest = AssignTiers(now, groups, s);
        Move(viewers, dest);
        Cleanup(now, viewers, dest);
    }

    private void MeasureSource(double now, long bytes)
    {
        if (_lastSourceBytes >= 0 && now > _lastTick && bytes >= _lastSourceBytes)
        {
            double kbps = (bytes - _lastSourceBytes) * 8 / (now - _lastTick);
            _sourceKbps = double.IsNaN(_sourceKbps) ? kbps : _sourceKbps * 0.8 + kbps * 0.2;
        }
        _lastSourceBytes = bytes;
        _lastTick = now;
    }

    private void Classify(double now, IAdaptViewer v, AdaptSettings s)
    {
        if (!_states.TryGetValue(v.Id, out var st)) _states[v.Id] = st = new State { LastChange = now };
        st.Samples.Enqueue((now, v.VideoSent, v.VideoDropped, v.AckedBytes));
        while (st.Samples.Count > 1 && now - st.Samples.Peek().T > s.WindowMs) st.Samples.Dequeue();

        var first = st.Samples.Peek();
        double span = now - first.T;
        long sent = v.VideoSent - first.Sent, dropped = v.VideoDropped - first.Dropped;
        double drop = sent + dropped > 0 ? (double)dropped / (sent + dropped) : 0;
        double goodput = span > 0 ? (v.AckedBytes - first.Acked) * 8 / span : 0;   // bytes/ms * 8 = kbit/s
        bool onTier = !(v.PendingRendition ?? v.Rendition).IsSource;
        double since = now - st.LastChange;
        bool measured = span >= s.WindowMs * 0.5;

        if (since >= MinDwellMs && (st.Forced || measured && drop >= s.DropThreshold))
        {
            st.Forced = false;
            double cap = goodput > 0 ? goodput : s.MinKbps;
            st.Capacity = onTier && !double.IsNaN(st.Capacity) ? Math.Min(st.Capacity, cap) : cap;
            if (!double.IsNaN(st.ProbeAt))
            {
                st.Backoff = Math.Min(st.Backoff * 2, MaxBackoffMs);   // the source still doesn't fit
                st.ProbeAt = double.NaN;
            }
            st.WantsTier = true;
            Changed(st, now);
            log.Info("adapt: viewer struggling", new("nick", v.Nick), new("drop_pct", Math.Round(drop * 100, 1)),
                new("kbps", Math.Round(cap)), new("on", v.Rendition.Label));
            return;
        }
        st.Forced = false;
        if (st.WantsTier && onTier && measured && dropped == 0 && sent > 0 && since >= st.Backoff)
        {
            var cur = v.PendingRendition ?? v.Rendition;
            st.ProbeAt = now;
            Changed(st, now);
            if (cur.Kbps >= MaxTierKbps(s) - 50)
            {
                st.WantsTier = false;   // as big as a re-encode gets: the source is the next step
                log.Info("adapt: trying the source again", new("nick", v.Nick), new("backoff_s", st.Backoff / 1000));
            }
            else
            {
                double next = (cur.Kbps + s.AudioKbps) / s.Headroom * StepUp;
                st.Capacity = double.IsNaN(st.Capacity) ? next : Math.Max(st.Capacity, next);
                log.Info("adapt: stepping up", new("nick", v.Nick), new("from", cur.Label), new("kbps", Math.Round(next)));
            }
            return;
        }
        if (!double.IsNaN(st.ProbeAt) && now - st.ProbeAt >= ProbeConfirmMs)
        {
            st.ProbeAt = double.NaN;
            st.Backoff = InitialBackoffMs;
            st.Capacity = double.NaN;
        }
    }

    private static void Changed(State st, double now)
    {
        st.LastChange = now;
        st.Samples.Clear();
    }

    /// <summary>Struggling viewers, fastest first, split into at most <see cref="AdaptSettings.MaxTranscoders"/> groups.</summary>
    private List<List<IAdaptViewer>> Groups(IReadOnlyList<IAdaptViewer> viewers, AdaptSettings s)
    {
        var want = new List<IAdaptViewer>();
        foreach (var v in viewers) if (_states[v.Id].WantsTier) want.Add(v);
        want.Sort((a, b) => Cap(b, s).CompareTo(Cap(a, s)));
        var groups = new List<List<IAdaptViewer>>();
        foreach (var v in want)
        {
            if (groups.Count == 0 || s.MaxTranscoders > 1 && Cap(groups[^1][0], s) > 2 * Cap(v, s)) groups.Add([]);
            groups[^1].Add(v);
        }
        while (groups.Count > Math.Max(1, s.MaxTranscoders))
        {
            groups[^2].AddRange(groups[^1]);   // fold the slowest groups together
            groups.RemoveAt(groups.Count - 1);
        }
        return groups;
    }

    private double Cap(IAdaptViewer v, AdaptSettings s)
    {
        double c = _states[v.Id].Capacity;
        return double.IsNaN(c) ? s.MinKbps : c;
    }

    /// <summary>A group's bitrate: its slowest member's capacity with headroom, less audio, within bounds.</summary>
    internal int Target(List<IAdaptViewer> g, AdaptSettings s)
    {
        double slowest = double.PositiveInfinity;
        foreach (var v in g) slowest = Math.Min(slowest, Cap(v, s));
        double k = Math.Min(slowest * s.Headroom - s.AudioKbps, MaxTierKbps(s));
        k = Math.Max(k, s.MinKbps);
        return (int)(Math.Round(k / 50) * 50);
    }

    /// <summary>The biggest re-encode worth making: 80% of the source (beyond that, just watch the source).</summary>
    private double MaxTierKbps(AdaptSettings s) =>
        double.IsNaN(_sourceKbps) ? double.PositiveInfinity : Math.Max(s.MinKbps, _sourceKbps * 0.8);

    /// <summary>Match groups to transcoders (starting or retuning as needed); returns where each viewer belongs.</summary>
    private Dictionary<int, Rendition?> AssignTiers(double now, List<List<IAdaptViewer>> groups, AdaptSettings s)
    {
        var dest = new Dictionary<int, Rendition?>();
        var used = new HashSet<Tier>();
        foreach (var g in groups)
        {
            int target = Target(g, s);
            Tier? best = null;
            foreach (var t in _tiers)
            {
                if (t.Dead || used.Contains(t) || IsReplacement(t)) continue;
                if (best is null || Math.Abs(t.R.Kbps - target) < Math.Abs(best.R.Kbps - target)) best = t;
            }
            if (best is null)
            {
                best = TryStart(now, target, s);
                if (best is null) continue;
            }
            used.Add(best);
            // Retune: a new encoder at the new bitrate; the old one serves until the new one has a keyframe.
            bool off = Math.Abs(best.R.Kbps - target) > RetuneRatio * target;
            if (off && now - best.R.CreatedAt >= RetuneMinAgeMs && (best.ReplacedBy is null || best.ReplacedBy.Dead))
            {
                var repl = TryStart(now, target, s, replacing: best);
                if (repl is not null)
                {
                    best.ReplacedBy = repl;
                    log.Info("adapt: retuning", new("from", best.R.Label), new("to", repl.R.Label));
                }
            }
            if (best.ReplacedBy is { Dead: false } r) used.Add(r);
            var to = best.ReplacedBy is { Dead: false, R.Ready: true } nr ? nr.R : best.R.Ready ? best.R : null;
            foreach (var v in g) dest[v.Id] = to;
        }
        return dest;
    }

    private bool IsReplacement(Tier t)
    {
        foreach (var o in _tiers) if (o.ReplacedBy == t) return true;
        return false;
    }

    private Tier? TryStart(double now, int kbps, AdaptSettings s, Tier? replacing = null)
    {
        int live = 0;
        double upload = double.IsNaN(_sourceKbps) ? 0 : _sourceKbps;
        foreach (var t in _tiers)
        {
            if (t.Dead) continue;
            live++;
            if (t != replacing) upload += t.R.Kbps + s.AudioKbps;
        }
        if (live >= s.MaxTranscoders + (replacing is null ? 0 : 1) || now < _noStartUntil) return null;
        if (s.BudgetKbps > 0 && upload + kbps + s.AudioKbps > s.BudgetKbps)
        {
            if (now >= _budgetLoggedAt + StartRetryMs)
            {
                _budgetLoggedAt = now;
                log.Warn("adapt: re-encode doesn't fit the upload budget", new("kbps", (long)kbps),
                    new("in_use_kbps", Math.Round(upload)), new("budget_kbps", (long)s.BudgetKbps));
            }
            return null;
        }
        try
        {
            var t = new Tier(start(kbps));
            _tiers.Add(t);
            return t;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Error("adapt: cannot start ffmpeg", new("ffmpeg", s.Ffmpeg), new("error", e.Message));
            _noStartUntil = now + StartRetryMs;
            return null;
        }
    }

    private void Move(IReadOnlyList<IAdaptViewer> viewers, Dictionary<int, Rendition?> dest)
    {
        foreach (var v in viewers)
        {
            var st = _states[v.Id];
            Rendition? to = st.WantsTier ? dest.GetValueOrDefault(v.Id) : source;
            if (to is null) continue;                                   // its re-encode isn't ready yet: stay put
            if (to.Failed) to = source;
            if (ReferenceEquals(v.PendingRendition ?? v.Rendition, to)) continue;
            if (!to.IsSource) log.Info("adapt: moving viewer", new("nick", v.Nick), new("to", to.Label));
            v.SwitchTo(to);
        }
    }

    private void Cleanup(double now, IReadOnlyList<IAdaptViewer> viewers, Dictionary<int, Rendition?> dest)
    {
        for (int i = _tiers.Count - 1; i >= 0; i--)
        {
            var t = _tiers[i];
            bool inUse = false;
            foreach (var v in viewers)
                if (ReferenceEquals(v.Rendition, t.R) || ReferenceEquals(v.PendingRendition, t.R)) { inUse = true; break; }
            if (!inUse) foreach (var d in dest.Values) if (ReferenceEquals(d, t.R)) { inUse = true; break; }
            if (!inUse && IsReplacement(t)) inUse = true;   // warming up for a retune

            if (t.Dead) { Remove(i, viewers); continue; }
            if (inUse) { t.IdleSince = double.NaN; continue; }
            if (double.IsNaN(t.IdleSince)) t.IdleSince = now;
            // A retuned-away encoder stops as soon as nobody's on it; an unused one after a grace period.
            if (t.ReplacedBy is { Dead: false } || now - t.IdleSince >= IdleStopMs) Remove(i, viewers);
        }
    }

    private void Remove(int i, IReadOnlyList<IAdaptViewer> viewers)
    {
        var t = _tiers[i];
        _tiers.RemoveAt(i);
        foreach (var o in _tiers) if (o.ReplacedBy == t) o.ReplacedBy = null;
        foreach (var v in viewers)
            if (ReferenceEquals(v.Rendition, t.R) || ReferenceEquals(v.PendingRendition, t.R)) v.SwitchTo(source);
        stop(t.R);
    }

    /// <summary>Stop every re-encode (adaptation turned off, or the stream ended).</summary>
    public void StopAll(IReadOnlyList<IAdaptViewer> viewers)
    {
        for (int i = _tiers.Count - 1; i >= 0; i--) Remove(i, viewers);
        foreach (var st in _states.Values) { st.WantsTier = false; st.Capacity = double.NaN; st.ProbeAt = double.NaN; }
    }
}
