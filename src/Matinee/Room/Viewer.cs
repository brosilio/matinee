using System.Collections.Concurrent;
using Httpgrl;
using Matinee.Media;
using Matinee.Net;

namespace Matinee.Room;

/// <summary>
/// One connected viewer: a WebSocket, a control queue and a cursor into the shared media ring.
///
/// <b>Sending.</b> A single sender task writes everything. Control messages (pongs, chat) always go first.
/// Media is read from the ring at the viewer's cursor, and the same pre-built frame bytes go to every viewer.
///
/// <b>Dropping (live mode).</b> The client acknowledges the newest media timestamp it has received (in its pings),
/// so we know how much media is in flight, kernel buffers included. When the in-flight backlog passes
/// <see cref="StreamSettings.CongestionMs"/>, video is dropped up to the next keyframe it can afford (audio
/// keeps flowing unless things are much worse). If the cursor falls behind the live edge by more than that, it
/// jumps straight to the newest keyframe. A slow viewer never builds up delay: they miss content instead.
///
/// <b>Renditions.</b> The viewer watches one <see cref="Rendition"/> (the source, or a re-encode for slow links).
/// Switching happens at the new rendition's first keyframe after what this viewer has already been sent, so time
/// never jumps backwards: video holds until that keyframe, and audio already sent is never sent twice.
/// </summary>
public sealed class Viewer : IAdaptViewer
{
    private const int MaxQueuedControl = 4096;

    private readonly Stream _stream;
    private readonly Room _room;
    private readonly ConcurrentQueue<byte[]> _control = new();
    private int _controlCount;
    private readonly AsyncSignal _signal = new();
    private readonly CancellationTokenSource _cts;

    // Media cursor state: sender task only.
    private long _cursor = -1;
    private bool _waitKey = true;
    private MediaFrame? _sentVideoCfg, _sentAudioCfg;
    private double _lastSentTs = double.NaN;
    private double _lastVideoTs = double.NegativeInfinity, _lastVideoDts = double.NegativeInfinity, _lastAudioTs = double.NegativeInfinity;
    private Rendition _rendition;
    private Rendition? _pending;
    private double _switchSince = double.NaN;
    private const double SwitchTimeoutMs = 4000;

    // Throughput measurement: (timestamp, cumulative media bytes) of frames sent, so an ack of a timestamp tells
    // how many bytes the client has received. Written by the sender, read by the reader (acks), under _sentLock.
    private readonly Lock _sentLock = new();
    private readonly double[] _sentTs = new double[4096];
    private readonly long[] _sentBytes = new long[4096];
    private int _sentHead, _sentCount;
    private long _mediaBytes, _ackedBytes;

    // Written by the reader task, read by the sender.
    private double _ackTs = double.NaN;
    private double _ackAt;

    public int Id { get; }
    public string Remote { get; }
    public ChatUser? User { get; internal set; }
    public string? Token { get; internal set; }
    public string Nick => User?.Nick ?? "?";
    public double JoinedAt { get; } = Clock.NowMs;

    /// <summary>How far ahead of its playhead the client says it has media buffered (ms), from its last ping.</summary>
    public double BufferedAheadMs { get; internal set; } = double.NaN;
    internal double BufferReportAt { get; set; } = double.NegativeInfinity;

    /// <summary>
    /// Sync mode ignores this viewer's buffer reports until then. A viewer that just joined (or whose media just
    /// restarted) starts with an empty buffer; that's filling up, not falling behind, and mustn't pause the room.
    /// </summary>
    internal double SyncGraceUntil { get; private set; } = Clock.NowMs + SyncGraceMs;
    internal const double SyncGraceMs = 5000;

    public long FramesSent { get; private set; }
    public long FramesDropped { get; private set; }
    /// <summary>Video frames sent and dropped by the congestion policy (switch gaps aren't drops).</summary>
    public long VideoSent { get; private set; }
    public long VideoDropped { get; private set; }
    /// <summary>Media bytes the client has acknowledged receiving.</summary>
    public long AckedBytes => Interlocked.Read(ref _ackedBytes);
    /// <summary>The rendition this viewer is watching.</summary>
    public Rendition Rendition => Volatile.Read(ref _rendition);
    /// <summary>A switch is waiting for the target's next keyframe.</summary>
    public Rendition? PendingRendition => Volatile.Read(ref _pending);
    public long BytesSent { get; private set; }
    public int Skips { get; private set; }

    internal Viewer(int id, Stream stream, Room room, string remote, CancellationToken ct)
    {
        Id = id;
        _rendition = room.Source;
        _stream = stream;
        _room = room;
        Remote = remote;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public CancellationToken Cancellation => _cts.Token;

    /// <summary>Queue a pre-built control frame. Never blocks; a viewer that can't keep up with chat is cut.</summary>
    public void Send(byte[] frame)
    {
        if (Interlocked.Increment(ref _controlCount) > MaxQueuedControl)
        {
            Kick("too slow");
            return;
        }
        _control.Enqueue(frame);
        _signal.Set();
    }

    /// <summary>New media is available.</summary>
    internal void Wake() => _signal.Set();

    internal void Ack(double ts)
    {
        if (!double.IsFinite(ts)) return;
        Volatile.Write(ref _ackTs, ts);
        Volatile.Write(ref _ackAt, Clock.NowMs);
        lock (_sentLock)
        {
            // Bytes up to the newest sent frame at or before the acked timestamp have arrived.
            long acked = -1;
            int cap = _sentTs.Length;
            while (_sentCount > 0 && _sentTs[_sentHead] <= ts)
            {
                acked = _sentBytes[_sentHead];
                _sentHead = (_sentHead + 1) % cap;
                _sentCount--;
            }
            if (acked > _ackedBytes) Interlocked.Exchange(ref _ackedBytes, acked);
        }
    }

    void IAdaptViewer.SwitchTo(Rendition r) => SwitchTo(r);

    /// <summary>Move to <paramref name="r"/> at its next keyframe (see class remarks).</summary>
    internal void SwitchTo(Rendition r)
    {
        if (ReferenceEquals(r, Rendition) && PendingRendition is null) return;
        Volatile.Write(ref _pending, ReferenceEquals(r, Rendition) ? null : r);
        _signal.Set();
    }

    public string? KickReason { get; private set; }

    public void Kick(string reason)
    {
        KickReason ??= reason;
        // Never cancel inline: Kick can be called under the room lock, and cancellation runs continuations.
        try { _ = _cts.CancelAsync(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Restart media from the newest keyframe (e.g. the stream restarted).</summary>
    internal void ResetMedia()
    {
        SyncGraceUntil = Clock.NowMs + SyncGraceMs;
        Volatile.Write(ref _resetRequested, 1);
        _signal.Set();
    }
    private int _resetRequested;

    internal async Task SendLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await DrainControlAsync(ct).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
                {
                    _cursor = -1;
                    _waitKey = true;
                    _sentVideoCfg = _sentAudioCfg = null;
                    _lastSentTs = double.NaN;
                    _lastVideoTs = _lastVideoDts = _lastAudioTs = double.NegativeInfinity;
                    Volatile.Write(ref _rendition, _room.Source);
                    Volatile.Write(ref _pending, null);
                    _switchSince = double.NaN;
                }

                bool sentAny = false;
                for (int budget = 0; budget < 64 && _control.IsEmpty; budget++)
                {
                    var f = NextMedia();
                    if (f is null) break;
                    await SendMediaAsync(f, ct).ConfigureAwait(false);
                    sentAny = true;
                }
                if (sentAny || !_control.IsEmpty) continue;
                await _signal.WaitAsync(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { Kick("connection lost"); }
        catch (ObjectDisposedException) { Kick("connection lost"); }
        finally
        {
            // Best effort goodbye; the connection may already be dead.
            if (KickReason is not null)
            {
                try
                {
                    using var t = new CancellationTokenSource(1000);
                    await _stream.WriteAsync(WsFrame.Close(1000, KickReason), t.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or NotSupportedException) { }
            }
        }
    }

    private async ValueTask DrainControlAsync(CancellationToken ct)
    {
        while (_control.TryDequeue(out var frame))
        {
            Interlocked.Decrement(ref _controlCount);
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            BytesSent += frame.Length;
        }
    }

    private async ValueTask SendMediaAsync(MediaFrame f, CancellationToken ct)
    {
        var cfg = f.Config;
        if (cfg is not null)
        {
            ref var sent = ref f.IsVideo ? ref _sentVideoCfg : ref _sentAudioCfg;
            if (!ReferenceEquals(sent, cfg))
            {
                await _stream.WriteAsync(cfg.Wire, ct).ConfigureAwait(false);
                BytesSent += cfg.Wire.Length;
                if (f.IsVideo) _sentVideoCfg = cfg; else _sentAudioCfg = cfg;
            }
        }
        await _stream.WriteAsync(f.Wire, ct).ConfigureAwait(false);
        BytesSent += f.Wire.Length;
        FramesSent++;
        if (f.IsVideo) { VideoSent++; _lastVideoTs = Math.Max(_lastVideoTs, f.Ts); _lastVideoDts = Math.Max(_lastVideoDts, f.Dts); }
        else _lastAudioTs = Math.Max(_lastAudioTs, f.Ts);
        if (double.IsNaN(_lastSentTs) || f.Ts > _lastSentTs) _lastSentTs = f.Ts;
        lock (_sentLock)
        {
            _mediaBytes += f.Wire.Length;
            int cap = _sentTs.Length;
            if (_sentCount == cap) { _sentHead = (_sentHead + 1) % cap; _sentCount--; }
            int i = (_sentHead + _sentCount) % cap;
            _sentTs[i] = _lastSentTs;
            _sentBytes[i] = _mediaBytes;
            _sentCount++;
        }
    }

    /// <summary>
    /// Apply a pending rendition switch if the target has a keyframe after what we've sent. Until then, video from
    /// the current rendition is held (there's no point sending frames we're about to leave); give up after a while.
    /// </summary>
    private bool SwitchPending()
    {
        var target = Volatile.Read(ref _pending);
        if (target is null) return false;
        double now = Clock.NowMs;
        if (double.IsNaN(_switchSince)) _switchSince = now;
        long k = target.Ring.KeyAfter(_lastVideoTs);
        if (k >= 0)
        {
            Volatile.Write(ref _rendition, target);
            Volatile.Write(ref _pending, null);
            _switchSince = double.NaN;
            _cursor = k;
            _waitKey = false;
            using (var j = JsonFrame.Start("tier"))
            {
                j.W.WriteString("name"u8, target.Label);
                j.W.WriteNumber("kbps"u8, target.Kbps);
                Send(j.Finish());
            }
            return false;
        }
        if (now - _switchSince > SwitchTimeoutMs || target.Failed)
        {
            Volatile.Write(ref _pending, null);
            _switchSince = double.NaN;
            return false;
        }
        return true;
    }

    /// <summary>The next frame to send, or null if there's nothing (yet). Applies the drop policy.</summary>
    private MediaFrame? NextMedia()
    {
        bool holdVideo = SwitchPending();
        var ring = _rendition.Ring;
        var s = _room.Settings;
        double horizon = _room.SendHorizon();   // sync mode: don't run further ahead of the playhead than this

        for (int guard = 0; guard < 4096; guard++)
        {
            if (_cursor < 0 || _cursor < ring.Tail)
            {
                long start = _room.StartSeq(ring);
                if (start < 0) return null;
                if (_cursor >= 0) { Skips++; }
                _cursor = start;
                _waitKey = false;
            }

            var f = ring.Get(_cursor);
            if (f is null) return null;                     // caught up
            if (f.Ts > horizon) return null;

            if (f.Kind is FrameKind.VideoConfig or FrameKind.AudioConfig) { _cursor++; continue; }   // sent on demand
            // Never send a time twice (after a rendition switch the new ring overlaps what was sent).
            // (Video by decode time: with B-frames, presentation time isn't monotonic.)
            if (f.IsVideo ? f.Dts <= _lastVideoDts || holdVideo : f.Ts <= _lastAudioTs) { _cursor++; continue; }

            double inflight = InFlightMs(f);
            if (_room.Mode == StreamMode.Live)
            {
                double lag = ring.HeadTs - f.Ts;
                if (lag > s.CongestionMs)
                {
                    long k = ring.LatestKey();
                    if (k > _cursor)
                    {
                        FramesDropped += k - _cursor;
                        VideoDropped += k - _cursor;
                        Skips++;
                        _cursor = k;
                        continue;
                    }
                }

                bool congested = inflight > s.CongestionMs;
                if (f.IsVideo)
                {
                    if (congested || (_waitKey && !f.IsKey))
                    {
                        _waitKey = true;
                        FramesDropped++;
                        VideoDropped++;
                        _cursor++;
                        continue;
                    }
                    _waitKey = false;
                }
                else if (inflight > s.CongestionMs * 2)
                {
                    FramesDropped++;
                    _cursor++;
                    continue;
                }
            }
            _cursor++;
            return f;
        }
        return null;
    }

    /// <summary>
    /// Media sent but not yet acknowledged, in ms. Before the first ack (or if acks stop) assume everything sent
    /// since the last ack (or since joining) is in flight, aged by the time that passed.
    /// </summary>
    private double InFlightMs(MediaFrame next)
    {
        if (double.IsNaN(_lastSentTs)) return 0;
        double ack = Volatile.Read(ref _ackTs);
        if (double.IsNaN(ack)) return Math.Max(0, Clock.NowMs - JoinedAt - 2000);
        // Acks arrive every ~300 ms, so the measure is stale by up to that much; credit it.
        double stale = Clock.NowMs - Volatile.Read(ref _ackAt);
        return Math.Max(0, _lastSentTs - ack - Math.Min(stale, 400));
    }

    internal async Task<WsMessage?> ReadAsync(WsReader reader) => await reader.ReadAsync(_cts.Token).ConfigureAwait(false);

    internal void DisposeCts() => _cts.Dispose();
}
