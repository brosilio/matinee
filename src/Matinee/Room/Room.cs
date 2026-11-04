using System.Security.Cryptography;
using System.Text;
using Httpgrl;
using Matinee.Media;
using Matinee.Net;

namespace Matinee.Room;

/// <summary>
/// The single room: the media ring, the viewers, the chat floor and history, and the shared timeline.
///
/// <b>Timeline.</b> Every client plays media time <c>m + (serverNow - w) * r</c>, from the last "tl" message. In
/// live mode that's simply <c>serverNow - delay</c>: every client converges on the same playhead because they
/// all sync their clocks to ours. Sync mode moves the anchor (pause: r = 0; catch up: r = 1.05).
/// </summary>
public sealed class Room : IFloorSink, IAsyncDisposable
{
    private const int HistorySize = 200;
    private const int HelloTimeoutMs = 10_000;
    private const int IdleTimeoutMs = 30_000;

    private readonly Lock _lock = new();
    private readonly List<Viewer> _viewers = new();
    private readonly Floor _floor;
    private readonly Queue<HistoryLine> _history = new();
    private long _lineSeq;
    private int _nextId;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _ticker;
    private readonly SyncTimeline _timeline = new();

    public MediaRing Ring { get; }
    public StreamSettings Settings { get; private set; }
    public StreamMode Mode => Settings.Mode;
    public bool Live { get; private set; }

    private readonly record struct HistoryLine(long Seq, string? Nick, string Text, LineKind Kind, double Ts);

    public Room(StreamSettings settings, ILogger log)
    {
        Settings = settings;
        _log = log;
        Ring = new MediaRing(retainMs: settings.EffectiveBufferMs);
        _floor = new Floor(this);
        ResetTimeline();
        _ticker = TickLoop();
    }

    public int ViewerCount { get { lock (_lock) return _viewers.Count; } }

    public void Apply(StreamSettings s)
    {
        lock (_lock)
        {
            bool timeline = s.Mode != Settings.Mode || s.DelayMs != Settings.DelayMs;
            Settings = s;
            Ring.RetainMs = s.EffectiveBufferMs;
            if (timeline) { ResetTimeline(); BroadcastLocked(TimelineFrame()); }
        }
    }

    // ---- Media ----

    /// <summary>Called by ingest for every frame.</summary>
    public void Append(MediaFrame f)
    {
        Ring.Append(f);
        lock (_lock)
            foreach (var v in _viewers) v.Wake();
    }

    public void StreamStarted()
    {
        lock (_lock)
        {
            Live = true;
            ResetTimeline();
            foreach (var v in _viewers) v.ResetMedia();
            BroadcastLocked(StatusFrame());
            BroadcastLocked(TimelineFrame());
        }
        Notice("the stream is live");
    }

    public void StreamEnded()
    {
        lock (_lock)
        {
            Live = false;
            BroadcastLocked(StatusFrame());
        }
        Ring.Wake();
        Notice("the stream ended");
    }

    /// <summary>Where a viewer with no position starts: the keyframe nearest the playhead (or the newest one).</summary>
    internal long StartSeq()
    {
        if (Mode == StreamMode.Sync)
        {
            long k = Ring.KeyAtOrBefore(Playhead(Clock.NowMs));
            if (k >= 0) return k;
        }
        return Ring.LatestKey();
    }

    /// <summary>Media time a viewer may be sent up to. Live mode: everything.</summary>
    internal double SendHorizon() => Mode == StreamMode.Sync ? Playhead(Clock.NowMs) + 5000 : double.PositiveInfinity;

    /// <summary>The shared timeline's current rate (0: paused, 1.05: catching up).</summary>
    public double Rate { get { lock (_lock) return _timeline.Rate; } }

    public double Playhead(double now)
    {
        lock (_lock) return _timeline.Playhead(now);
    }

    private void ResetTimeline()
    {
        _timeline.Reset(Clock.NowMs, Settings.DelayMs);
    }

    // ---- Viewers ----

    /// <summary>Serve one upgraded WebSocket connection until it ends.</summary>
    public async Task RunViewerAsync(Stream stream, string remote, CancellationToken ct)
    {
        var reader = new WsReader(stream);
        Viewer v;
        lock (_lock) v = new Viewer(++_nextId, stream, this, remote, ct);
        Task? sender = null;
        bool joined = false;
        try
        {
            // Hello first, within a deadline.
            ClientMessage hello;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(HelloTimeoutMs);
                var m = await reader.ReadAsync(t.Token).ConfigureAwait(false);
                if (m is not { IsText: true } msg || !ClientMessage.TryParse(msg.Payload, out hello) || hello.T != "hello") return;
            }

            if (!CheckPassword(hello.Pass))
            {
                _log.Warn("viewer: bad password", new LogField("remote", remote));
                await Task.Delay(1000, ct).ConfigureAwait(false);
                await RefuseAsync(stream, "auth", "wrong password", ct).ConfigureAwait(false);
                return;
            }
            string? nick = ValidNick(hello.Nick);
            if (nick is null)
            {
                await RefuseAsync(stream, "nick", "pick a nick: 1-24 letters, digits, _ or -", ct).ConfigureAwait(false);
                return;
            }
            string? err = Join(v, nick, hello.Tok);
            if (err is not null)
            {
                await RefuseAsync(stream, err == "full" ? "full" : "nick", err == "full" ? "the room is full" : "that nick is taken", ct).ConfigureAwait(false);
                return;
            }
            joined = true;
            sender = Task.Run(v.SendLoopAsync, CancellationToken.None);

            // Reader loop.
            var bucket = new TokenBucket(40, 80);
            while (true)
            {
                WsMessage? m;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(v.Cancellation))
                {
                    idle.CancelAfter(IdleTimeoutMs);
                    m = await reader.ReadAsync(idle.Token).ConfigureAwait(false);
                }
                if (m is null) break;
                var msg = m.Value;
                if (msg.IsClose) break;
                if (msg.IsPing) { v.Send(WsFrame.Build(WsFrame.OpPong, msg.Payload)); continue; }
                if (!bucket.Take(Clock.NowMs)) { v.Kick("slow down"); break; }
                if (!msg.IsText || !ClientMessage.TryParse(msg.Payload, out var cm)) continue;
                OnMessage(v, cm);
            }
        }
        catch (OperationCanceledException) { }
        catch (WsProtocolException e) { _log.Debug("viewer: protocol error", new("remote", remote), new("error", e.Message)); }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException) { }
        finally
        {
            if (joined) Leave(v);
            v.Kick(v.KickReason ?? "bye");
            if (sender is not null)
            {
                try { await sender.ConfigureAwait(false); } catch (Exception e) { _log.Error("viewer: sender failed", new LogField("error", e.ToString())); }
            }
            v.DisposeCts();
        }
    }

    private static async Task RefuseAsync(Stream stream, string code, string text, CancellationToken ct)
    {
        byte[] f;
        using (var j = JsonFrame.Start("error"))
        {
            j.W.WriteString("code"u8, code);
            j.W.WriteString("x"u8, text);
            f = j.Finish();
        }
        try
        {
            await stream.WriteAsync(f, ct).ConfigureAwait(false);
            await stream.WriteAsync(WsFrame.Close(4000, code), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private bool CheckPassword(string? given)
    {
        var want = Settings.Password;
        if (want is null) return true;
        if (given is null) return false;
        // Compare digests so the comparison is constant-time whatever the lengths.
        Span<byte> a = stackalloc byte[32], b = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(want), a);
        SHA256.HashData(Encoding.UTF8.GetBytes(given), b);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    internal static string? ValidNick(string? nick)
    {
        if (nick is null) return null;
        nick = nick.Trim().TrimStart('@');
        if (nick.Length is 0 or > 24) return null;
        foreach (char c in nick)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return null;
        return nick;
    }

    private string? Join(Viewer v, string nick, string? token)
    {
        Viewer? replaced = null;
        lock (_lock)
        {
            foreach (var o in _viewers)
            {
                if (!string.Equals(o.Nick, nick, StringComparison.OrdinalIgnoreCase)) continue;
                // Same browser tab reconnecting while its old connection hasn't timed out yet: take over.
                if (token is { Length: >= 16 } && o.Token == token) { replaced = o; break; }
                return "taken";
            }
            if (replaced is null && _viewers.Count >= Settings.MaxViewers) return "full";
            if (replaced is not null)
            {
                _viewers.Remove(replaced);
                if (replaced.User is not null) _floor.Leave(replaced.User);
            }
            v.User = new ChatUser(v.Id, nick);
            v.Token = token;
            _viewers.Add(v);

            // Welcome, timeline, status, history and floor go out before anything else.
            using (var j = JsonFrame.Start("welcome"))
            {
                j.W.WriteNumber("id"u8, v.Id);
                j.W.WriteString("nick"u8, nick);
                j.W.WriteNumber("now"u8, Clock.NowMs);
                j.W.WriteString("mode"u8, Mode == StreamMode.Sync ? "sync" : "live");
                j.W.WriteNumber("delay"u8, Settings.DelayMs);
                j.W.WriteNumber("maxDelay"u8, Settings.MaxDelayMs);
                v.Send(j.Finish());
            }
            v.Send(TimelineFrame());
            v.Send(StatusFrame());
            v.Send(HistoryFrame());
            v.Send(FloorFrame());
        }
        replaced?.Kick("replaced by a new connection");
        _log.Info("viewer: joined", new("nick", nick), new("remote", v.Remote), new("viewers", (long)ViewerCount));
        if (replaced is null) Notice($"@{nick} joined");
        lock (_lock) BroadcastLocked(StatusFrame());
        return null;
    }

    private void Leave(Viewer v)
    {
        bool removed;
        lock (_lock)
        {
            removed = _viewers.Remove(v);
            if (removed && v.User is not null) _floor.Leave(v.User);
            if (removed) BroadcastLocked(StatusFrame());
        }
        if (removed)
        {
            _log.Info("viewer: left", new("nick", v.Nick), new("reason", v.KickReason ?? "closed"),
                new("sent", v.FramesSent), new("dropped", v.FramesDropped), new("skips", (long)v.Skips));
            Notice($"@{v.Nick} left");
        }
    }

    private void OnMessage(Viewer v, in ClientMessage m)
    {
        switch (m.T)
        {
            case "ping":
            {
                if (m.HasA) v.Ack(m.A);
                if (m.HasA && m.HasB)
                {
                    v.BufferedAheadMs = Math.Clamp(m.B, -60_000, 600_000);
                    v.BufferReportAt = Clock.NowMs;
                }
                using var j = JsonFrame.Start("pong");
                j.W.WriteNumber("c"u8, m.C);
                j.W.WriteNumber("s"u8, Clock.NowMs);
                v.Send(j.Finish());
                return;
            }
            case "type":
                lock (_lock) if (v.User is not null) _floor.SetTyping(v.User, m.On, Clock.NowMs);
                return;
            case "in":
            {
                if (v.User is null || m.X is null) return;
                var kind = m.K switch { "word" => InputKind.Word, "edit" => InputKind.Edit, "send" => InputKind.Send, _ => (InputKind)255 };
                if ((byte)kind == 255) return;
                string text = Sanitize(m.X);
                lock (_lock) _floor.Input(v.User, text, kind, Clock.NowMs);
                return;
            }
        }
    }

    /// <summary>No control characters in chat (newlines included: a line is a line).</summary>
    private static string Sanitize(string s)
    {
        if (s.Length > Floor.MaxText) s = s[..Floor.MaxText];
        foreach (char c in s)
        {
            if (char.IsControl(c) || c is (char)0x2028 or (char)0x2029)
            {
                var sb = new StringBuilder(s.Length);
                foreach (char d in s) sb.Append(char.IsControl(d) || d is (char)0x2028 or (char)0x2029 ? ' ' : d);
                return sb.ToString();
            }
        }
        return s;
    }

    // ---- Chat (IFloorSink: always called under _lock) ----

    void IFloorSink.Line(ChatUser? user, string text, LineKind kind) => AddLineLocked(user?.Nick, text, kind);

    void IFloorSink.Took(ChatUser user, string text)
    {
        using var j = JsonFrame.Start("took");
        j.W.WriteString("x"u8, text);
        var f = j.Finish();
        foreach (var v in _viewers)
            if (v.User == user) { v.Send(f); break; }
    }

    void IFloorSink.StateChanged() => BroadcastLocked(FloorFrame());

    /// <summary>A system line in chat ("@richard has slow net; pausing...").</summary>
    public void Notice(string text)
    {
        lock (_lock) AddLineLocked(null, text, LineKind.System);
    }

    private void AddLineLocked(string? nick, string text, LineKind kind)
    {
        var line = new HistoryLine(++_lineSeq, nick, text, kind, Clock.NowMs);
        _history.Enqueue(line);
        while (_history.Count > HistorySize) _history.Dequeue();
        using var j = JsonFrame.Start("line");
        WriteLine(j.W, line);
        BroadcastLocked(j.Finish());
    }

    private static void WriteLine(System.Text.Json.Utf8JsonWriter w, in HistoryLine l)
    {
        w.WriteNumber("i"u8, l.Seq);
        if (l.Nick is null) w.WriteNull("n"u8); else w.WriteString("n"u8, l.Nick);
        w.WriteString("x"u8, l.Text);
        w.WriteString("k"u8, l.Kind switch { LineKind.System => "sys", LineKind.Clash => "clash", _ => "say" });
        w.WriteNumber("ts"u8, Math.Round(l.Ts));
    }

    private byte[] HistoryFrame()
    {
        using var j = JsonFrame.Start("hist");
        j.W.WriteStartArray("l"u8);
        foreach (var l in _history)
        {
            j.W.WriteStartObject();
            WriteLine(j.W, l);
            j.W.WriteEndObject();
        }
        j.W.WriteEndArray();
        return j.Finish();
    }

    private byte[] FloorFrame()
    {
        using var j = JsonFrame.Start("floor");
        if (_floor.Owner is null) j.W.WriteNull("o"u8); else j.W.WriteString("o"u8, _floor.Owner.Nick);
        j.W.WriteString("x"u8, _floor.LiveText);
        j.W.WriteBoolean("c"u8, _floor.Clash);
        j.W.WriteStartArray("ty"u8);
        foreach (var u in _floor.Typing) if (u != _floor.Owner) j.W.WriteStringValue(u.Nick);
        j.W.WriteEndArray();
        j.W.WriteStartArray("w"u8);
        foreach (var u in _floor.Wanting) j.W.WriteStringValue(u.Nick);
        j.W.WriteEndArray();
        return j.Finish();
    }

    private byte[] TimelineFrame()
    {
        using var j = JsonFrame.Start("tl");
        j.W.WriteNumber("w"u8, _timeline.Wall);
        j.W.WriteNumber("m"u8, _timeline.Media);
        j.W.WriteNumber("r"u8, _timeline.Rate);
        return j.Finish();
    }

    private byte[] StatusFrame()
    {
        using var j = JsonFrame.Start("status");
        j.W.WriteBoolean("live"u8, Live);
        j.W.WriteNumber("viewers"u8, _viewers.Count);
        j.W.WriteStartArray("who"u8);
        foreach (var v in _viewers) j.W.WriteStringValue(v.Nick);
        j.W.WriteEndArray();
        return j.Finish();
    }

    private void BroadcastLocked(byte[] frame)
    {
        foreach (var v in _viewers) v.Send(frame);
    }

    // ---- Housekeeping ----

    private async Task TickLoop()
    {
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    double now = Clock.NowMs;
                    _floor.Tick(now);
                    TickSyncLocked(now);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void TickSyncLocked(double now)
    {
        if (!Live || Mode != StreamMode.Sync) return;

        bool targetPresent = false, targetFresh = false;
        double targetAhead = double.NaN;
        int slowId = 0;
        string? slowNick = null;
        double slowest = SyncTimeline.LowBufferMs;
        foreach (var v in _viewers)
        {
            bool fresh = now - v.BufferReportAt <= SyncTimeline.ReportFreshMs && double.IsFinite(v.BufferedAheadMs);
            if (v.Id == _timeline.PausedViewerId)
            {
                targetPresent = true;
                targetFresh = fresh;
                targetAhead = v.BufferedAheadMs;
            }
            if (fresh && now >= v.SyncGraceUntil && v.BufferedAheadMs < slowest)
            {
                slowest = v.BufferedAheadMs;
                slowId = v.Id;
                slowNick = v.Nick;
            }
        }

        var e = _timeline.Tick(now, Settings.DelayMs, Settings.MaxDelayMs,
            targetPresent, targetFresh, targetAhead, slowId, slowNick);
        if (e.Kind == SyncEventKind.None) return;

        if (e.TimelineChanged) BroadcastLocked(TimelineFrame());
        switch (e.Kind)
        {
            case SyncEventKind.Pause:
                AddLineLocked(null,
                    $"@{e.Nick} has slow net; pausing for up to {Math.Max(1, (int)Math.Ceiling(e.PauseBudgetMs / 1000))}s so they can catch up",
                    LineKind.System);
                break;
            case SyncEventKind.Resume:
                AddLineLocked(null, "buffer recovered; resuming at 1.05× until everyone is back at the live delay", LineKind.System);
                break;
            case SyncEventKind.DropViewer:
                foreach (var v in _viewers)
                {
                    if (v.Id != e.ViewerId) continue;
                    v.BufferedAheadMs = double.NaN; // don't select it again while its reconnect is in flight
                    v.Kick("reconnecting at the shared playhead");
                    break;
                }
                AddLineLocked(null, $"@{e.Nick} could not catch up; reconnecting them at the shared playhead", LineKind.System);
                break;
            case SyncEventKind.CaughtUp:
                AddLineLocked(null, "the room is back at the live delay", LineKind.System);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_lock) foreach (var v in _viewers) v.Kick("server shutting down");
        await _ticker.ConfigureAwait(false);
    }

    /// <summary>Per-viewer message rate limit.</summary>
    private struct TokenBucket(double perSecond, double burst)
    {
        private readonly double _rate = perSecond, _burst = burst;
        private double _tokens = burst, _at = Clock.NowMs;

        public bool Take(double now)
        {
            _tokens = Math.Min(_burst, _tokens + (now - _at) / 1000 * _rate);
            _at = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
