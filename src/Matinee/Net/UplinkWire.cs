using System.Buffers.Binary;
using Matinee.Media;

namespace Matinee.Net;

/// <summary>
/// The uplink between two matinee instances: a home instance (OBS, GPU) serving a cloud instance (viewers).
///
/// One WebSocket carries every stream, so they all share one clock mapping on the cloud side and stay frame-aligned.
/// Binary messages are media:
/// <code>
///   0  u16  stream id (0: the source; others: re-encodes the cloud asked for)
///   2  f64  decode timestamp, sender's clock (ms)
///  10  ...  the viewer-protocol message (see MediaFrame): kind, flags, reserved, timestamp, payload
/// </code>
/// Text messages are JSON control:
/// <list type="bullet">
/// <item>cloud → home: <c>{"t":"hello","key":".."}</c> first, then <c>{"t":"want","id":n,"kbps":k}</c>,
/// <c>{"t":"drop","id":n}</c>, <c>{"t":"ping"}</c></item>
/// <item>home → cloud: <c>{"t":"welcome","live":b}</c>, <c>{"t":"live","on":b}</c>, <c>{"t":"failed","id":n}</c>,
/// <c>{"t":"pong"}</c></item>
/// </list>
/// </summary>
public static class UplinkWire
{
    public const int PrefixSize = 10;

    /// <summary>A complete WebSocket frame carrying <paramref name="f"/> on stream <paramref name="sid"/>.</summary>
    public static byte[] Frame(int sid, MediaFrame f, double dts)
    {
        int wsHeader = f.Wire.Length - f.PayloadLength - MediaFrame.HeaderSize;
        var msg = f.Wire.AsSpan(wsHeader);
        int len = PrefixSize + msg.Length;
        var buf = new byte[WsFrame.HeaderLength(len) + len];
        int h = WsFrame.WriteHeader(buf, WsFrame.OpBinary, len);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(h), (ushort)sid);
        BinaryPrimitives.WriteDoubleLittleEndian(buf.AsSpan(h + 2), dts);
        msg.CopyTo(buf.AsSpan(h + PrefixSize));
        return buf;
    }

    public readonly ref struct Parsed
    {
        public int Sid { get; init; }
        public double Dts { get; init; }
        public FrameKind Kind { get; init; }
        public bool Key { get; init; }
        public double Ts { get; init; }
        public ReadOnlySpan<byte> Payload { get; init; }
    }

    public static bool TryParse(ReadOnlySpan<byte> m, out Parsed p)
    {
        p = default;
        if (m.Length < PrefixSize + MediaFrame.HeaderSize) return false;
        var v = m[PrefixSize..];
        var kind = (FrameKind)v[0];
        if (kind is not (FrameKind.Video or FrameKind.Audio or FrameKind.VideoConfig or FrameKind.AudioConfig)) return false;
        double dts = BinaryPrimitives.ReadDoubleLittleEndian(m[2..]);
        double ts = BinaryPrimitives.ReadDoubleLittleEndian(v[4..]);
        if (!double.IsFinite(dts) || !double.IsFinite(ts)) return false;
        p = new Parsed
        {
            Sid = BinaryPrimitives.ReadUInt16LittleEndian(m),
            Dts = dts,
            Kind = kind,
            Key = (v[1] & MediaFrame.FlagKey) != 0,
            Ts = ts,
            Payload = v[MediaFrame.HeaderSize..],
        };
        return true;
    }

    /// <summary>Unpack a config payload (codec, sample rate, channels, description).</summary>
    public static bool TryParseConfig(ReadOnlySpan<byte> p, out string codec, out int rate, out int channels, out ReadOnlySpan<byte> description)
    {
        codec = ""; rate = 0; channels = 0; description = default;
        if (p.Length < 1) return false;
        int n = p[0];
        if (n == 0 || p.Length < 1 + n + 5) return false;
        foreach (byte b in p.Slice(1, n)) if (b is < 0x20 or > 0x7E) return false;
        codec = System.Text.Encoding.ASCII.GetString(p.Slice(1, n));
        rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[(1 + n)..]);
        channels = p[1 + n + 4];
        description = p[(1 + n + 5)..];
        return true;
    }
}
