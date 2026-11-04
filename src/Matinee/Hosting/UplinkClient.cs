using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Httpgrl;
using Matinee.Media;
using Matinee.Net;
using Matinee.Room;

namespace Matinee.Hosting;

/// <summary>
/// The cloud side of an uplink (see <see cref="UplinkWire"/>): pulls the stream from a home instance instead of
/// taking RTMP ingest, and asks it for re-encodes when viewers here need them (this box only relays bytes).
///
/// Usually the URL is a location on our own httpgrl that <c>proxy_pass</c>es into a reverse tunnel the home box
/// dialed in on, so the home box needs no open ports and httpgrl does the TLS, client certificates and redialing.
/// It can also point straight at the home box (<c>wss://</c>) when that's reachable.
///
/// Every stream arrives on one connection and goes through one <see cref="ClockMapper"/>: the source drives it, the
/// re-encodes follow, so they stay frame-aligned with the source on our clock and viewers switch seamlessly.
/// Reconnects forever, backing off to 10 s.
/// </summary>
internal sealed class UplinkClient : IAsyncDisposable
{
    private const int MaxMessage = 16 << 20;
    private const int ConnectTimeoutMs = 10_000;

    private readonly Room.Room _room;
    private readonly Uri _url;
    private readonly string _key;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<int, (Sink Sink, Remote Encoder)> _remote = new();
    private Task? _loop;
    private Session? _session;
    private int _nextId;
    private bool _live;

    public UplinkClient(Room.Room room, Uri url, string key, ILogger log)
    {
        _room = room;
        _url = url;
        _key = key;
        _log = log;
    }

    public bool Connected { get { lock (_lock) return _session is not null; } }

    public void Start() => _loop = LoopAsync();

    private sealed class Session(ClientWebSocket ws, ILogger log)
    {
        public readonly ClientWebSocket Ws = ws;
        public readonly ClockMapper Clock = new(log, "uplink");
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Outbox = new();
        public readonly AsyncSignal Signal = new();
        public void Send(string json) { Outbox.Enqueue(json); Signal.Set(); }
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        int backoff = 1000;
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    t.CancelAfter(ConnectTimeoutMs);
                    await ws.ConnectAsync(_url, t.Token).ConfigureAwait(false);
                }
                _log.Info("uplink: connected to home", new LogField("url", Redacted()));
                lastError = null;
                backoff = 1000;
                await RunAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or HttpRequestException or InvalidDataException)
            {
                if (e.Message != lastError) _log.Warn("uplink: home unreachable", new("url", Redacted()), new("error", e.Message));
                lastError = e.Message;
            }
            finally
            {
                Disconnected();
            }
            try { await Task.Delay(backoff + Random.Shared.Next(backoff / 4), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, 10_000);
        }
    }

    private string Redacted() => $"{_url.Scheme}://{_url.Authority}{_url.AbsolutePath}";

    private async Task RunAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var session = new Session(ws, _log);
        session.Send(Hello());
        var sender = SendLoopAsync(session, ct);
        var source = new Sink(_room.Append, drives: true);
        var buf = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int len = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (len == buf.Length)
                    {
                        if (buf.Length >= MaxMessage) throw new InvalidDataException("uplink message too large");
                        Array.Resize(ref buf, Math.Min(buf.Length * 2, MaxMessage));
                    }
                    r = await ws.ReceiveAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
                    len += r.Count;
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.CloseStatusDescription == "refused") _log.Error("uplink: home refused our key (check uplink_pull / uplink_key)");
                    break;
                }
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    Control(session, buf.AsSpan(0, len));
                    continue;
                }
                if (!UplinkWire.TryParse(buf.AsSpan(0, len), out var p)) continue;
                if (p.Sid == 0) source.Accept(session.Clock, p);
                else
                {
                    Sink? sink;
                    lock (_lock) sink = _remote.TryGetValue(p.Sid, out var e) ? e.Sink : null;
                    sink?.Accept(session.Clock, p);
                }
            }
        }
        finally
        {
            session.Signal.Set();
            try { await sender.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private string Hello()
    {
        var b = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(b))
        {
            w.WriteStartObject();
            w.WriteString("t", "hello");
            w.WriteString("key", _key);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(b.WrittenSpan);
    }

    private async Task SendLoopAsync(Session s, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && s.Ws.State == WebSocketState.Open)
            {
                while (s.Outbox.TryDequeue(out var m))
                    await s.Ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                await s.Signal.WaitAsync(5000, ct).ConfigureAwait(false);
                if (s.Outbox.IsEmpty) s.Outbox.Enqueue("""{"t":"ping"}""");
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
    }

    private void Control(Session session, ReadOnlySpan<byte> json)
    {
        string? type = null;
        bool? live = null;
        int id = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.ValueTextEquals("t")) { r.Read(); type = r.GetString(); }
                else if (r.ValueTextEquals("live") || r.ValueTextEquals("on")) { r.Read(); live = r.TokenType == JsonTokenType.True; }
                else if (r.ValueTextEquals("id")) { r.Read(); id = r.TryGetInt32(out var v) ? v : 0; }
            }
        }
        catch (JsonException) { return; }
        switch (type)
        {
            case "welcome":
                lock (_lock) _session = session;
                SetLive(live == true);
                break;
            case "live":
                SetLive(live == true);
                break;
            case "failed":
                lock (_lock) if (_remote.TryGetValue(id, out var e)) e.Encoder.MarkFailed();
                break;
        }
    }

    private void SetLive(bool on)
    {
        if (on == _live) return;
        _live = on;
        if (on) _room.StreamStarted(); else _room.StreamEnded();
    }

    private void Disconnected()
    {
        lock (_lock)
        {
            _session = null;
            foreach (var e in _remote.Values) e.Encoder.MarkFailed();
            _remote.Clear();
        }
        SetLive(false);
    }

    /// <summary>Ask home for a re-encode (the adaptation policy's "start"). Throws if home isn't connected.</summary>
    public Rendition Request(int kbps)
    {
        Session session;
        int id;
        var ring = new MediaRing(retainMs: _room.Ring.RetainMs);
        lock (_lock)
        {
            session = _session ?? throw new InvalidOperationException("the uplink to home isn't connected");
            id = _nextId = _nextId % ushort.MaxValue + 1;
            var enc = new Remote(this, session, id);
            _remote[id] = (new Sink(f => { ring.Append(f); _room.WakeAll(); }, drives: false), enc);
            session.Send($$"""{"t":"want","id":{{id}},"kbps":{{kbps}}}""");
            _log.Info("uplink: asking home for a re-encode", new("id", (long)id), new("kbps", (long)kbps));
            return new Rendition(ring, kbps, Transcoder.HeightFor(kbps), $"{Transcoder.HeightFor(kbps)}p · {kbps} kbit/s") { Encoder = enc };
        }
    }

    private void Drop(Session session, int id)
    {
        lock (_lock)
        {
            _remote.Remove(id);
            if (ReferenceEquals(_session, session)) session.Send($$"""{"t":"drop","id":{{id}}}""");
        }
    }

    /// <summary>A re-encode made at home. It fails if home says so or the connection it was asked on goes away.</summary>
    private sealed class Remote(UplinkClient client, Session session, int id) : IEncoder
    {
        private volatile bool _failed;
        public bool Failed => _failed;
        public void MarkFailed() => _failed = true;
        public Task StopAsync()
        {
            _failed = true;
            client.Drop(session, id);
            return Task.CompletedTask;
        }
    }

    /// <summary>One stream's frames, re-stamped onto our clock and kept in order (as IngestSession does for RTMP).</summary>
    private sealed class Sink(Action<MediaFrame> append, bool drives)
    {
        private MediaFrame? _video, _audio;
        private double _lastVideoDts = double.NegativeInfinity, _lastAudio = double.NegativeInfinity;
        private bool _needKey = true;

        public void Accept(ClockMapper clock, in UplinkWire.Parsed p)
        {
            if (p.Kind is FrameKind.VideoConfig or FrameKind.AudioConfig)
            {
                if (!UplinkWire.TryParseConfig(p.Payload, out var codec, out int rate, out int ch, out var desc)) return;
                ref var cur = ref p.Kind == FrameKind.VideoConfig ? ref _video : ref _audio;
                if (cur is not null && cur.Payload.SequenceEqual(p.Payload)) return;
                cur = MediaFrame.CreateConfig(p.Kind, codec, rate, ch, desc, clock.MapFollowing(p.Dts));
                append(cur);
                return;
            }
            double dts = drives ? clock.Map(p.Dts) : clock.MapFollowing(p.Dts);
            if (p.Kind == FrameKind.Video)
            {
                if (_video is null) return;
                if (ClockMapper.Behind(ref dts, _lastVideoDts)) { _needKey = true; return; }
                if (_needKey && !p.Key) return;
                _needKey = false;
                _lastVideoDts = dts;
                append(MediaFrame.Create(FrameKind.Video, p.Key, dts + (p.Ts - p.Dts), p.Payload, _video, dts));
            }
            else
            {
                if (_audio is null) return;
                if (ClockMapper.Behind(ref dts, _lastAudio)) return;
                _lastAudio = dts;
                append(MediaFrame.Create(FrameKind.Audio, true, dts, p.Payload, _audio));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (Exception) { } }
        _stop.Dispose();
    }
}
