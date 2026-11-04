using System.Buffers.Binary;
using System.Text;
using Matinee.Net;

namespace Matinee.Media;

public enum FrameKind : byte { Video = 1, Audio = 2, VideoConfig = 3, AudioConfig = 4 }

/// <summary>
/// One unit of media, already encoded as a complete WebSocket binary message. It's built once at ingest and the
/// same bytes are written to every viewer.
///
/// Wire layout of the WebSocket payload (little-endian):
/// <code>
///   0  u8   kind (FrameKind)
///   1  u8   flags (bit0: keyframe)
///   2  u16  reserved
///   4  f64  timestamp, server-clock milliseconds
///  12  ...  payload
/// </code>
/// Media payloads are the codec's bitstream as WebCodecs wants it (AVCC length-prefixed NAL units for H.264,
/// OBUs for AV1, raw AAC or Opus frames). Config payloads are:
/// <code>u8 codecLen, codec (ASCII, a WebCodecs codec string), u32 sampleRate, u8 channels, description...</code>
/// </summary>
public sealed class MediaFrame
{
    public const int HeaderSize = 12;
    public const byte FlagKey = 1;

    public FrameKind Kind { get; }
    public bool IsKey { get; }
    public double Ts { get; }
    /// <summary>The whole WebSocket frame (header included).</summary>
    public byte[] Wire { get; }
    public int PayloadLength { get; }
    /// <summary>The decoder config this frame needs (video frames: video config; audio frames: audio config).</summary>
    public MediaFrame? Config { get; }
    /// <summary>Position in the ring, assigned on append.</summary>
    public long Seq { get; internal set; } = -1;

    private MediaFrame(FrameKind kind, bool key, double ts, byte[] wire, int payloadLength, MediaFrame? config)
    {
        Kind = kind; IsKey = key; Ts = ts; Wire = wire; PayloadLength = payloadLength; Config = config;
    }

    public bool IsVideo => Kind == FrameKind.Video;
    public bool IsAudio => Kind == FrameKind.Audio;

    public static MediaFrame Create(FrameKind kind, bool key, double ts, ReadOnlySpan<byte> payload, MediaFrame? config = null)
    {
        int msgLen = HeaderSize + payload.Length;
        int hdr = WsFrame.HeaderLength(msgLen);
        var wire = new byte[hdr + msgLen];
        WsFrame.WriteHeader(wire, WsFrame.OpBinary, msgLen);
        var m = wire.AsSpan(hdr);
        m[0] = (byte)kind;
        m[1] = key ? FlagKey : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(m[4..], ts);
        payload.CopyTo(m[HeaderSize..]);
        return new MediaFrame(kind, key, ts, wire, payload.Length, config);
    }

    public static MediaFrame CreateConfig(FrameKind kind, string codec, int sampleRate, int channels, ReadOnlySpan<byte> description, double ts)
    {
        var codecBytes = Encoding.ASCII.GetBytes(codec);
        var payload = new byte[1 + codecBytes.Length + 4 + 1 + description.Length];
        payload[0] = (byte)codecBytes.Length;
        codecBytes.CopyTo(payload, 1);
        int o = 1 + codecBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), (uint)sampleRate);
        payload[o + 4] = (byte)channels;
        description.CopyTo(payload.AsSpan(o + 5));
        var f = Create(kind, true, ts, payload);
        f.Codec = codec;
        return f;
    }

    /// <summary>Codec string, for config frames.</summary>
    public string? Codec { get; private set; }
}
