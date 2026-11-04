using System.Buffers.Binary;
using System.Security.Cryptography;
using Httpgrl;

namespace Matinee.Rtmp;

/// <summary>What the session needs from its host: who may publish, and where media goes.</summary>
public interface IRtmpHandler
{
    /// <summary>Called on "publish". Return false to refuse (bad key, already live...).</summary>
    bool OnPublish(RtmpSession session, string app, string streamName);
    void OnVideo(RtmpSession session, uint timestamp, ReadOnlySpan<byte> body);
    void OnAudio(RtmpSession session, uint timestamp, ReadOnlySpan<byte> body);
    void OnUnpublish(RtmpSession session);
}

/// <summary>
/// The server side of one RTMP connection, enough for a publisher such as OBS or ffmpeg: the simple handshake,
/// the chunk stream, and the connect/createStream/publish commands. Everything is bounded because the ingest
/// port is reachable by anyone who can reach the server.
/// </summary>
public sealed class RtmpSession(Stream stream, IRtmpHandler handler, ILogger log, string remote)
{
    private const int HandshakeSize = 1536;
    private const int MaxMessage = 8 << 20;
    private const int MaxChunkStreams = 64;
    private const int OutChunkSize = 4096;

    private readonly byte[] _in = new byte[64 * 1024];
    private int _start, _end;
    private int _inChunkSize = 128;
    private readonly Dictionary<int, ChunkState> _chunks = new();
    private long _received;
    private long _lastAck;
    private uint _peerWindow;               // the client asked us to ack every _peerWindow bytes
    private string _app = "";
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Remote => remote;
    /// <summary>Bytes received so far (the host's idle watchdog reads it).</summary>
    public long Received => Volatile.Read(ref _received);
    public bool Publishing { get; private set; }
    /// <summary>Set by the server: tears the connection down (a newer publisher took over).</summary>
    public Action? Abort { get; set; }
    public string? StreamName { get; private set; }

    private sealed class ChunkState
    {
        public uint Timestamp, Delta;
        public int Length;
        public byte Type;
        public uint StreamId;
        public bool ExtendedTs;
        public byte[]? Buf;
        public int Filled;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await HandshakeAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var msg = await ReadMessageAsync(ct).ConfigureAwait(false);
                if (msg is null) break;
                await HandleAsync(msg.Value, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Publishing)
            {
                Publishing = false;
                handler.OnUnpublish(this);
            }
        }
    }

    // ---- Handshake (simple: S1 random, S2 echoes C1) ----

    private async Task HandshakeAsync(CancellationToken ct)
    {
        await FillAsync(1 + HandshakeSize, ct).ConfigureAwait(false);
        if (_in[_start] != 3) throw new InvalidDataException($"unsupported RTMP version {_in[_start]}");
        var reply = new byte[1 + HandshakeSize * 2];
        reply[0] = 3;
        // S1: time(4) zero(4) random(1528)
        RandomNumberGenerator.Fill(reply.AsSpan(9, HandshakeSize - 8));
        // S2: echo C1
        _in.AsSpan(_start + 1, HandshakeSize).CopyTo(reply.AsSpan(1 + HandshakeSize));
        _start += 1 + HandshakeSize;
        await stream.WriteAsync(reply, ct).ConfigureAwait(false);
        await FillAsync(HandshakeSize, ct).ConfigureAwait(false);   // C2: ignored
        _start += HandshakeSize;
    }

    // ---- Chunk stream ----

    private readonly record struct Message(byte Type, uint StreamId, uint Timestamp, byte[] Buf, int Length)
    {
        public ReadOnlySpan<byte> Body => Buf.AsSpan(0, Length);
    }

    private async ValueTask<Message?> ReadMessageAsync(CancellationToken ct)
    {
        while (true)
        {
            if (!await TryFillAsync(1, ct).ConfigureAwait(false)) return null;
            int fmt = _in[_start] >> 6;
            int csid = _in[_start] & 0x3F;
            int basic = 1;
            if (csid == 0) { await FillAsync(2, ct).ConfigureAwait(false); csid = 64 + _in[_start + 1]; basic = 2; }
            else if (csid == 1) { await FillAsync(3, ct).ConfigureAwait(false); csid = 64 + _in[_start + 1] + (_in[_start + 2] << 8); basic = 3; }

            int mh = fmt switch { 0 => 11, 1 => 7, 2 => 3, _ => 0 };
            await FillAsync(basic + mh, ct).ConfigureAwait(false);

            if (!_chunks.TryGetValue(csid, out var cs))
            {
                if (fmt != 0) throw new InvalidDataException($"chunk stream {csid} starts without a full header");
                if (_chunks.Count >= MaxChunkStreams) throw new InvalidDataException("too many chunk streams");
                cs = new ChunkState();
                _chunks[csid] = cs;
            }

            var h = _in.AsSpan(_start + basic, mh);
            bool newMessage = cs.Buf is null || cs.Filled == 0;
            uint tsField = 0;
            if (fmt <= 2) tsField = (uint)((h[0] << 16) | (h[1] << 8) | h[2]);
            if (fmt <= 1)
            {
                cs.Length = (h[3] << 16) | (h[4] << 8) | h[5];
                cs.Type = h[6];
                if (cs.Length > MaxMessage) throw new InvalidDataException("RTMP message too large");
            }
            if (fmt == 0) cs.StreamId = BinaryPrimitives.ReadUInt32LittleEndian(h[7..]);
            _start += basic + mh;

            if (fmt <= 2) cs.ExtendedTs = tsField == 0xFFFFFF;
            uint ext = 0;
            if (cs.ExtendedTs)
            {
                await FillAsync(4, ct).ConfigureAwait(false);
                ext = BinaryPrimitives.ReadUInt32BigEndian(_in.AsSpan(_start));
                _start += 4;
            }
            uint tsValue = cs.ExtendedTs ? ext : tsField;

            if (newMessage)
            {
                switch (fmt)
                {
                    case 0: cs.Timestamp = tsValue; cs.Delta = 0; break;
                    case 1: case 2: cs.Delta = tsValue; cs.Timestamp += tsValue; break;
                    default: cs.Timestamp += cs.Delta; break;
                }
                if (cs.Buf is null || cs.Buf.Length < cs.Length) cs.Buf = new byte[Math.Max(cs.Length, 256)];
                cs.Filled = 0;
            }

            // A chunk may be bigger than our input buffer (chunk sizes go up to 16 MB): copy it as it arrives.
            int take = Math.Min(_inChunkSize, cs.Length - cs.Filled);
            while (take > 0)
            {
                if (_start == _end) await FillAsync(1, ct).ConfigureAwait(false);
                int n = Math.Min(take, _end - _start);
                _in.AsSpan(_start, n).CopyTo(cs.Buf.AsSpan(cs.Filled));
                _start += n;
                cs.Filled += n;
                take -= n;
            }

            if (cs.Filled >= cs.Length)
            {
                var m = new Message(cs.Type, cs.StreamId, cs.Timestamp, cs.Buf!, cs.Length);
                cs.Filled = 0;
                // Media buffers are handed off and consumed synchronously, so the buffer can be reused; protocol
                // messages are parsed immediately too.
                return m;
            }
        }
    }

    private async ValueTask HandleAsync(Message m, CancellationToken ct)
    {
        switch (m.Type)
        {
            case 1:   // Set Chunk Size
                if (m.Length < 4) return;
                int size = (int)(BinaryPrimitives.ReadUInt32BigEndian(m.Body) & 0x7FFFFFFF);
                if (size < 1 || size > 1 << 24) throw new InvalidDataException("bad chunk size");
                _inChunkSize = size;
                return;
            case 2:   // Abort
                if (m.Length >= 4 && _chunks.TryGetValue((int)BinaryPrimitives.ReadUInt32BigEndian(m.Body), out var a)) a.Filled = 0;
                return;
            case 3: case 4: case 6: return;   // ack, user control, peer bandwidth
            case 5:   // Window Acknowledgement Size
                if (m.Length >= 4) _peerWindow = BinaryPrimitives.ReadUInt32BigEndian(m.Body);
                return;
            case 8:
                if (Publishing) handler.OnAudio(this, m.Timestamp, m.Body);
                return;
            case 9:
                if (Publishing) handler.OnVideo(this, m.Timestamp, m.Body);
                return;
            case 18: case 15: return;   // data (@setDataFrame/onMetaData): not needed, configs come in-band
            case 20:
                await CommandAsync(Amf0.DecodeAll(m.Body), m.StreamId, ct).ConfigureAwait(false);
                return;
            case 17:   // AMF3 command: first byte is an AMF3 marker (0 means AMF0 follows)
                if (m.Length > 0) await CommandAsync(Amf0.DecodeAll(m.Body[1..]), m.StreamId, ct).ConfigureAwait(false);
                return;
            default: return;
        }
    }

    private async ValueTask CommandAsync(List<object?> args, uint streamId, CancellationToken ct)
    {
        if (args.Count < 2 || args[0] is not string name) return;
        double txn = args[1] is double d ? d : 0;
        switch (name)
        {
            case "connect":
            {
                if (args.Count > 2 && args[2] is AmfObject o) _app = o.Str("app") ?? "";
                await SendProtocolAsync(5, 5_000_000, ct).ConfigureAwait(false);          // window ack size
                await SendPeerBandwidthAsync(ct).ConfigureAwait(false);
                await SendProtocolAsync(1, OutChunkSize, ct).ConfigureAwait(false);       // our chunk size
                var w = new Amf0.Writer().String("_result").Number(txn)
                    .Object(("fmsVer", "FMS/3,0,1,123"), ("capabilities", 31.0))
                    .Object(("level", "status"), ("code", "NetConnection.Connect.Success"),
                            ("description", "Connection succeeded."), ("objectEncoding", 0.0));
                await SendCommandAsync(0, w, ct).ConfigureAwait(false);
                return;
            }
            case "releaseStream":
            case "FCPublish":
            case "getStreamLength":
                await SendCommandAsync(0, new Amf0.Writer().String("_result").Number(txn).Null().Null(), ct).ConfigureAwait(false);
                return;
            case "createStream":
                await SendCommandAsync(0, new Amf0.Writer().String("_result").Number(txn).Null().Number(1), ct).ConfigureAwait(false);
                return;
            case "publish":
            {
                string streamName = args.Count > 3 && args[3] is string s ? s : "";
                int q = streamName.IndexOf('?');
                if (q >= 0) streamName = streamName[..q];
                if (Publishing || !handler.OnPublish(this, _app, streamName))
                {
                    log.Warn("rtmp: publish refused", new("remote", remote), new("app", _app));
                    await SendStatusAsync("error", "NetStream.Publish.BadName", "Publish refused.", ct).ConfigureAwait(false);
                    throw new OperationCanceledException("publish refused");
                }
                StreamName = streamName;
                Publishing = true;
                await SendUserControlStreamBeginAsync(ct).ConfigureAwait(false);
                await SendStatusAsync("status", "NetStream.Publish.Start", "Publishing.", ct).ConfigureAwait(false);
                log.Info("rtmp: publishing", new("remote", remote), new("app", _app));
                return;
            }
            case "FCUnpublish":
            case "deleteStream":
            case "closeStream":
                if (Publishing)
                {
                    Publishing = false;
                    handler.OnUnpublish(this);
                }
                return;
            default:
                return;   // play, receiveAudio... are not for us
        }
    }

    // ---- Output (small control messages only; always on chunk stream 2/3, fmt 0, single chunk) ----

    private async ValueTask SendAsync(int csid, byte type, uint streamId, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        int chunks = Math.Max(1, (body.Length + OutChunkSize - 1) / OutChunkSize);
        var buf = new byte[12 + body.Length + chunks - 1];
        buf[0] = (byte)csid;
        // timestamp 0
        buf[4] = (byte)(body.Length >> 16); buf[5] = (byte)(body.Length >> 8); buf[6] = (byte)body.Length;
        buf[7] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), streamId);
        int o = 12, done = 0;
        while (done < body.Length)
        {
            if (done > 0) buf[o++] = (byte)(0xC0 | csid);
            int n = Math.Min(OutChunkSize, body.Length - done);
            body.Span.Slice(done, n).CopyTo(buf.AsSpan(o));
            o += n; done += n;
        }
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await stream.WriteAsync(buf.AsMemory(0, o), ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private ValueTask SendProtocolAsync(byte type, uint value, CancellationToken ct)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return SendAsync(2, type, 0, b, ct);
    }

    private ValueTask SendPeerBandwidthAsync(CancellationToken ct)
    {
        var b = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(b, 5_000_000);
        b[4] = 2;   // dynamic
        return SendAsync(2, 6, 0, b, ct);
    }

    private ValueTask SendUserControlStreamBeginAsync(CancellationToken ct)
    {
        var b = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(2), 1);
        return SendAsync(2, 4, 0, b, ct);
    }

    private ValueTask SendCommandAsync(uint streamId, Amf0.Writer w, CancellationToken ct) =>
        SendAsync(3, 20, streamId, w.Written.ToArray(), ct);

    private ValueTask SendStatusAsync(string level, string code, string description, CancellationToken ct) =>
        SendCommandAsync(1, new Amf0.Writer().String("onStatus").Number(0).Null()
            .Object(("level", level), ("code", code), ("description", description)), ct);

    // ---- Input buffering ----

    private async ValueTask FillAsync(int need, CancellationToken ct)
    {
        if (!await TryFillAsync(need, ct).ConfigureAwait(false)) throw new EndOfStreamException();
    }

    private async ValueTask<bool> TryFillAsync(int need, CancellationToken ct)
    {
        if (_end - _start >= need) return true;
        if (need > _in.Length) throw new InvalidDataException("read too large");
        if (_start > 0)
        {
            _in.AsSpan(_start, _end - _start).CopyTo(_in);
            _end -= _start;
            _start = 0;
        }
        while (_end - _start < need)
        {
            int n = await stream.ReadAsync(_in.AsMemory(_end), ct).ConfigureAwait(false);
            if (n == 0) return false;
            _end += n;
            Volatile.Write(ref _received, _received + n);
            if (_peerWindow > 0 && _received - _lastAck >= _peerWindow)
            {
                _lastAck = _received;
                await SendProtocolAsync(3, (uint)_received, ct).ConfigureAwait(false);
            }
        }
        return true;
    }
}
