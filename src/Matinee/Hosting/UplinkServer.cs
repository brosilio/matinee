using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Httpgrl;
using Matinee.Media;
using Matinee.Net;
using Matinee.Room;

namespace Matinee.Hosting;

/// <summary>
/// The home side of an uplink (see <see cref="UplinkWire"/>): serves one cloud instance its source stream, plus any
/// re-encodes it asks for, made here (where the CPU and GPU are).
///
/// The cloud connects to us, usually through httpgrl's reverse tunnel, so this box needs no open ports. If our
/// upload can't keep up, each stream skips ahead to its newest keyframe rather than falling behind.
/// </summary>
internal sealed class UplinkServer
{
    private const int HelloTimeoutMs = 10_000;
    private const double MaxLagMs = 2000;
    private const int MaxStreams = 16;

    private sealed class Out(int id, MediaRing ring, Rendition? rendition)
    {
        public readonly int Id = id;
        public readonly MediaRing Ring = ring;
        public readonly Rendition? Rendition = rendition;
        public long Cursor = -1;
        public MediaFrame? SentVideoCfg, SentAudioCfg;
    }

    private readonly Stream _stream;
    private readonly Engine _engine;
    private readonly ILogger _log;
    private readonly string _remote;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentQueue<byte[]> _control = new();
    private readonly AsyncSignal _signal = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Out> _streams = new();
    private readonly List<Task> _stopping = new();

    private UplinkServer(Stream stream, Engine engine, ILogger log, string remote, CancellationToken ct)
    {
        _stream = stream;
        _engine = engine;
        _log = log;
        _remote = remote;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public static async Task ServeAsync(Stream stream, Engine engine, ILogger log, string remote, CancellationToken ct)
    {
        var reader = new WsReader(stream, 64 * 1024);
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(HelloTimeoutMs);
            WsMessage? m;
            try { m = await reader.ReadAsync(t.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (m is not { IsText: true } hello || !ClientMessage.TryParse(hello.Payload, out var h) || h.T != "hello"
                || !KeyMatches(engine.Settings.UplinkKey, h.Key))
            {
                log.Warn("uplink: refused", new LogField("remote", remote));
                await Task.Delay(1000, ct).ConfigureAwait(false);
                await stream.WriteAsync(WsFrame.Close(4001, "refused"), ct).ConfigureAwait(false);
                return;
            }
        }
        var link = new UplinkServer(stream, engine, log, remote, ct);
        await link.RunAsync(reader).ConfigureAwait(false);
    }

    private static bool KeyMatches(string? want, string? given)
    {
        if (want is null || given is null) return false;
        Span<byte> a = stackalloc byte[32], b = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(want), a);
        SHA256.HashData(Encoding.UTF8.GetBytes(given), b);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private Room.Room Room => _engine.Room;

    private async Task RunAsync(WsReader reader)
    {
        _log.Info("uplink: cloud connected", new LogField("remote", _remote));
        lock (_lock) _streams[0] = new Out(0, Room.Ring, null);
        var writer = Task.Run(WriteLoopAsync, CancellationToken.None);
        try
        {
            while (true)
            {
                var m = await reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                if (m is null || m.Value.IsClose) break;
                if (m.Value.IsPing) { Send(WsFrame.Build(WsFrame.OpPong, m.Value.Payload)); continue; }
                if (m.Value.IsText && ClientMessage.TryParse(m.Value.Payload, out var c)) OnControl(c);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or EndOfStreamException or ObjectDisposedException or WsProtocolException) { }
        finally
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { await writer.ConfigureAwait(false); } catch (Exception e) { _log.Error("uplink: writer failed", new LogField("error", e.ToString())); }
            lock (_lock)
            {
                foreach (var o in _streams.Values)
                    if (o.Rendition?.Encoder is { } enc) _stopping.Add(enc.StopAsync());
                _streams.Clear();
            }
            await Task.WhenAll(_stopping).ConfigureAwait(false);
            _cts.Dispose();
            _log.Info("uplink: cloud disconnected", new LogField("remote", _remote));
        }
    }

    private void OnControl(in ClientMessage c)
    {
        int id = (int)c.Id;
        switch (c.T)
        {
            case "want":
            {
                var s = _engine.Settings.Adapt;
                int kbps = (int)c.Kbps;
                lock (_lock)
                {
                    if (id is <= 0 or > ushort.MaxValue || _streams.ContainsKey(id) || _streams.Count >= MaxStreams || kbps < 50 || kbps > 50_000)
                    {
                        Send(Json("failed", id));
                        return;
                    }
                }
                Rendition r;
                try { r = Transcoder.Start(Room.Ring, kbps, s, _signal.Set, _log); }
                catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _log.Error("uplink: cannot start ffmpeg", new("ffmpeg", s.Ffmpeg), new("error", e.Message));
                    Send(Json("failed", id));
                    return;
                }
                lock (_lock) _streams[id] = new Out(id, r.Ring, r);
                _log.Info("uplink: re-encode for the cloud", new("id", (long)id), new("rendition", r.Label));
                return;
            }
            case "drop":
                lock (_lock)
                {
                    if (id > 0 && _streams.Remove(id, out var o) && o.Rendition?.Encoder is { } enc)
                    {
                        _stopping.RemoveAll(t => t.IsCompleted);
                        _stopping.Add(enc.StopAsync());
                    }
                }
                return;
            case "ping":
                Send(Json("pong", 0));
                return;
        }
    }

    private static byte[] Json(string type, int id)
    {
        using var j = JsonFrame.Start(type);
        if (id != 0) j.W.WriteNumber("id"u8, id);
        return j.Finish();
    }

    private void Send(byte[] frame)
    {
        _control.Enqueue(frame);
        _signal.Set();
    }

    private async Task WriteLoopAsync()
    {
        var ct = _cts.Token;
        bool? live = null;
        var outs = new List<Out>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var appended = Room.Ring.NextAppend;
                bool nowLive = Room.Live;
                if (live != nowLive)
                {
                    using var j = JsonFrame.Start(live is null ? "welcome" : "live");
                    j.W.WriteBoolean(live is null ? "live"u8 : "on"u8, nowLive);
                    await _stream.WriteAsync(j.Finish(), ct).ConfigureAwait(false);
                    live = nowLive;
                }
                while (_control.TryDequeue(out var c)) await _stream.WriteAsync(c, ct).ConfigureAwait(false);

                outs.Clear();
                lock (_lock) outs.AddRange(_streams.Values);
                bool any = false;
                foreach (var o in outs)
                {
                    for (int n = 0; n < 32; n++)
                    {
                        var f = Next(o);
                        if (f is null) break;
                        var cfg = f.Config;
                        if (cfg is not null && !ReferenceEquals(cfg, f.IsVideo ? o.SentVideoCfg : o.SentAudioCfg))
                        {
                            await _stream.WriteAsync(UplinkWire.Frame(o.Id, cfg, f.Dts), ct).ConfigureAwait(false);
                            if (f.IsVideo) o.SentVideoCfg = cfg; else o.SentAudioCfg = cfg;
                        }
                        await _stream.WriteAsync(UplinkWire.Frame(o.Id, f, f.Dts), ct).ConfigureAwait(false);
                        any = true;
                    }
                }
                if (!any && _control.IsEmpty)
                    await Task.WhenAny(appended, _signal.WaitAsync(1000, ct)).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private MediaFrame? Next(Out o)
    {
        var ring = o.Ring;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (o.Cursor < 0 || o.Cursor < ring.Tail)
            {
                long k = ring.LatestKey();
                if (k < 0) return null;
                o.Cursor = k;
            }
            var f = ring.Get(o.Cursor);
            if (f is null) return null;
            // Our upload can't keep up: skip to the newest keyframe rather than fall behind.
            if (ring.HeadTs - f.Ts > MaxLagMs)
            {
                long k = ring.LatestKey();
                if (k > o.Cursor)
                {
                    _log.Warn("uplink: upload falling behind; skipping ahead", new LogField("stream", (long)o.Id));
                    o.Cursor = k;
                    continue;
                }
            }
            o.Cursor++;
            if (f.Kind is FrameKind.VideoConfig or FrameKind.AudioConfig) continue;   // sent with the frames that need them
            return f;
        }
        return null;
    }
}
