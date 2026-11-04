using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Matinee.Net;

/// <summary>RFC 6455 framing, server side: we send unmasked frames and require masked ones from clients.</summary>
public static class WsFrame
{
    public const byte OpContinuation = 0x0, OpText = 0x1, OpBinary = 0x2, OpClose = 0x8, OpPing = 0x9, OpPong = 0xA;

    public static int HeaderLength(int payloadLength) => payloadLength < 126 ? 2 : payloadLength <= 0xFFFF ? 4 : 10;

    /// <summary>Write a FIN frame header for an unmasked server frame. Returns its length.</summary>
    public static int WriteHeader(Span<byte> dst, byte opcode, int payloadLength)
    {
        dst[0] = (byte)(0x80 | opcode);
        if (payloadLength < 126) { dst[1] = (byte)payloadLength; return 2; }
        if (payloadLength <= 0xFFFF)
        {
            dst[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)payloadLength);
            return 4;
        }
        dst[1] = 127;
        BinaryPrimitives.WriteUInt64BigEndian(dst[2..], (ulong)payloadLength);
        return 10;
    }

    /// <summary>A complete server frame.</summary>
    public static byte[] Build(byte opcode, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderLength(payload.Length) + payload.Length];
        int h = WriteHeader(buf, opcode, payload.Length);
        payload.CopyTo(buf.AsSpan(h));
        return buf;
    }

    public static byte[] Text(string s) => Build(OpText, Encoding.UTF8.GetBytes(s));

    public static byte[] Close(ushort code, string reason)
    {
        var r = Encoding.UTF8.GetBytes(reason);
        if (r.Length > 120) r = r[..120];
        var p = new byte[2 + r.Length];
        BinaryPrimitives.WriteUInt16BigEndian(p, code);
        r.CopyTo(p, 2);
        return Build(OpClose, p);
    }
}

public static class WsHandshake
{
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Sec-WebSocket-Accept for a client key, or null if the key isn't 16 base64-encoded bytes.</summary>
    public static string? Accept(string? key)
    {
        if (key is null || key.Length != 24) return null;
        Span<byte> raw = stackalloc byte[18];
        if (!Convert.TryFromBase64String(key, raw, out int n) || n != 16) return null;
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(Encoding.ASCII.GetBytes(key + Guid), hash);
        return Convert.ToBase64String(hash);
    }
}

public readonly record struct WsMessage(byte Opcode, byte[] Buffer, int Length)
{
    public ReadOnlySpan<byte> Payload => Buffer.AsSpan(0, Length);
    public bool IsText => Opcode == WsFrame.OpText;
    public bool IsBinary => Opcode == WsFrame.OpBinary;
    public bool IsClose => Opcode == WsFrame.OpClose;
    public bool IsPing => Opcode == WsFrame.OpPing;
}

public sealed class WsProtocolException(string message, ushort code = 1002) : Exception(message)
{
    public ushort Code { get; } = code;
}

/// <summary>
/// Reads client messages from a WebSocket stream. Control frames are returned to the caller as they arrive
/// (even between fragments). One reader at a time; writing is the caller's business (frames are pre-built).
/// </summary>
public sealed class WsReader(Stream stream, int maxMessage = 16 * 1024)
{
    private readonly byte[] _in = new byte[8192];
    private int _start, _end;
    private byte[] _msg = new byte[1024];
    private int _msgLen;
    private byte _msgOp;
    private bool _inFragmented;

    /// <summary>The next message, or null at end of stream.</summary>
    public async ValueTask<WsMessage?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            if (!await FillAsync(2, ct)) return null;
            byte b0 = _in[_start], b1 = _in[_start + 1];
            bool fin = (b0 & 0x80) != 0;
            if ((b0 & 0x70) != 0) throw new WsProtocolException("reserved bits set");
            byte op = (byte)(b0 & 0x0F);
            if ((b1 & 0x80) == 0) throw new WsProtocolException("client frame not masked");
            long len = b1 & 0x7F;
            int hdr = 2;
            if (len == 126)
            {
                if (!await FillAsync(4, ct)) return null;
                len = BinaryPrimitives.ReadUInt16BigEndian(_in.AsSpan(_start + 2));
                hdr = 4;
            }
            else if (len == 127)
            {
                if (!await FillAsync(10, ct)) return null;
                len = (long)BinaryPrimitives.ReadUInt64BigEndian(_in.AsSpan(_start + 2));
                hdr = 10;
            }
            bool control = (op & 0x8) != 0;
            if (control && (len > 125 || !fin)) throw new WsProtocolException("bad control frame");
            if (op is not (WsFrame.OpContinuation or WsFrame.OpText or WsFrame.OpBinary or WsFrame.OpClose or WsFrame.OpPing or WsFrame.OpPong))
                throw new WsProtocolException("unknown opcode");
            if (len < 0 || len > maxMessage) throw new WsProtocolException("message too big", 1009);
            if (!await FillAsync(hdr + 4, ct)) return null;
            uint maskKey = BinaryPrimitives.ReadUInt32LittleEndian(_in.AsSpan(_start + hdr));
            _start += hdr + 4;

            int n = (int)len;
            if (control)
            {
                var p = new byte[n];
                await ReadPayloadAsync(p, 0, n, maskKey, ct);
                if (op == WsFrame.OpPong) continue;
                return new WsMessage(op, p, n);
            }

            if (op == WsFrame.OpContinuation)
            {
                if (!_inFragmented) throw new WsProtocolException("unexpected continuation");
            }
            else
            {
                if (_inFragmented) throw new WsProtocolException("expected continuation");
                _msgOp = op;
                _msgLen = 0;
            }
            if (_msgLen + n > maxMessage) throw new WsProtocolException("message too big", 1009);
            if (_msg.Length < _msgLen + n) Array.Resize(ref _msg, Math.Max(_msgLen + n, _msg.Length * 2));
            await ReadPayloadAsync(_msg, _msgLen, n, maskKey, ct);
            _msgLen += n;
            _inFragmented = !fin;
            if (fin)
            {
                var copy = _msg.AsSpan(0, _msgLen).ToArray();
                return new WsMessage(_msgOp, copy, copy.Length);
            }
        }
    }

    private async ValueTask ReadPayloadAsync(byte[] dst, int offset, int count, uint maskKey, CancellationToken ct)
    {
        int done = 0;
        while (done < count)
        {
            if (_start == _end && !await FillAsync(1, ct)) throw new EndOfStreamException();
            int take = Math.Min(count - done, _end - _start);
            _in.AsSpan(_start, take).CopyTo(dst.AsSpan(offset + done));
            _start += take;
            done += take;
        }
        Unmask(dst.AsSpan(offset, count), maskKey);
    }

    private static void Unmask(Span<byte> data, uint maskKey)
    {
        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(key, maskKey);
        for (int i = 0; i < data.Length; i++) data[i] ^= key[i & 3];
    }

    private async ValueTask<bool> FillAsync(int need, CancellationToken ct)
    {
        if (_end - _start >= need) return true;
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
        }
        return true;
    }
}
