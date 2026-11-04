using System.Buffers.Binary;

namespace Matinee.Media;

/// <summary>
/// FLV, as spoken to and from ffmpeg over pipes: the file header, tags, and tag bodies in the same legacy / Enhanced
/// RTMP layouts <see cref="IngestSession"/> parses.
/// </summary>
public static class Flv
{
    /// <summary>File header (audio + video) followed by PreviousTagSize0.</summary>
    public static ReadOnlySpan<byte> Header => [0x46, 0x4c, 0x56, 1, 5, 0, 0, 0, 9, 0, 0, 0, 0];

    public const byte TagAudio = 8, TagVideo = 9;
    public const int MaxTagBody = 8 << 20;

    /// <summary>A complete tag: header, body, and the trailing PreviousTagSize.</summary>
    public static byte[] Tag(byte type, uint timestamp, ReadOnlySpan<byte> body)
    {
        if (body.Length > 0xFF_FFFF) throw new InvalidDataException("FLV tag is too large");
        var tag = new byte[11 + body.Length + 4];
        tag[0] = type;
        tag[1] = (byte)(body.Length >> 16);
        tag[2] = (byte)(body.Length >> 8);
        tag[3] = (byte)body.Length;
        tag[4] = (byte)(timestamp >> 16);
        tag[5] = (byte)(timestamp >> 8);
        tag[6] = (byte)timestamp;
        tag[7] = (byte)(timestamp >> 24);
        body.CopyTo(tag.AsSpan(11));
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(11 + body.Length), (uint)(11 + body.Length));
        return tag;
    }

    /// <summary>
    /// A media or config frame as an FLV tag, with its timestamp relative to <paramref name="baseMs"/> (server clock).
    /// Null for codecs we don't carry, or a frame before the base.
    /// </summary>
    public static byte[]? FromFrame(MediaFrame f, double baseMs)
    {
        double rel = Math.Round(f.Dts - baseMs);
        if (rel < 0 || rel > uint.MaxValue) return null;
        uint ts = (uint)rel;
        bool config = f.Kind is FrameKind.VideoConfig or FrameKind.AudioConfig;
        string? codec = config ? f.Codec : f.Config?.Codec;
        if (codec is null) return null;
        var data = config ? f.Description : f.Payload;

        if (f.Kind is FrameKind.Video or FrameKind.VideoConfig)
        {
            int cts = (int)Math.Round(f.Ts - f.Dts);
            byte frameType = (byte)(config || f.IsKey ? 1 : 2);
            byte[] body;
            if (codec.StartsWith("avc1", StringComparison.Ordinal))
            {
                // Legacy AVC: [frameType<<4 | 7][AVCPacketType][composition time SI24][data]
                body = new byte[5 + data.Length];
                body[0] = (byte)((frameType << 4) | 7);
                body[1] = config ? (byte)0 : (byte)1;
                if (!config) { body[2] = (byte)(cts >> 16); body[3] = (byte)(cts >> 8); body[4] = (byte)cts; }
                data.CopyTo(body.AsSpan(5));
            }
            else if (codec.StartsWith("av01", StringComparison.Ordinal))
            {
                // Enhanced RTMP: [isEx | frameType<<4 | packetType]["av01"][data]; CodedFrames has no composition time.
                body = new byte[5 + data.Length];
                body[0] = (byte)(0x80 | (frameType << 4) | (config ? 0 : 1));
                "av01"u8.CopyTo(body.AsSpan(1));
                data.CopyTo(body.AsSpan(5));
            }
            else return null;
            return Tag(TagVideo, ts, body);
        }

        if (codec.StartsWith("mp4a", StringComparison.Ordinal))
        {
            // Legacy AAC: [0xAF: AAC, 44 kHz, 16 bit, stereo (fixed for AAC)][AACPacketType][data]
            var body = new byte[2 + data.Length];
            body[0] = 0xAF;
            body[1] = config ? (byte)0 : (byte)1;
            data.CopyTo(body.AsSpan(2));
            return Tag(TagAudio, ts, body);
        }
        if (codec == "opus")
        {
            // Enhanced RTMP audio: [9<<4 | packetType]["Opus"][data]
            var body = new byte[5 + data.Length];
            body[0] = (byte)(0x90 | (config ? 0 : 1));
            "Opus"u8.CopyTo(body.AsSpan(1));
            data.CopyTo(body.AsSpan(5));
            return Tag(TagAudio, ts, body);
        }
        return null;
    }

    /// <summary>Reads tags from an FLV stream (ffmpeg's stdout). Bounded: a broken stream throws, never balloons.</summary>
    public sealed class Reader(Stream stream)
    {
        private readonly byte[] _head = new byte[15];
        private bool _started;

        public readonly record struct FlvTag(byte Type, uint Timestamp, byte[] Body);

        /// <summary>The next audio or video tag, or null at the end of the stream.</summary>
        public async ValueTask<FlvTag?> NextAsync(CancellationToken ct)
        {
            if (!_started)
            {
                // FLV header: "FLV", version, flags, header length (u32), then PreviousTagSize0.
                if (!await Fill(_head.AsMemory(0, 9), ct).ConfigureAwait(false)) return null;
                if (_head[0] != 'F' || _head[1] != 'L' || _head[2] != 'V') throw new InvalidDataException("not an FLV stream");
                int skip = (int)BinaryPrimitives.ReadUInt32BigEndian(_head.AsSpan(5)) - 9 + 4;
                if (skip is < 4 or > 1024) throw new InvalidDataException("bad FLV header");
                var junk = new byte[skip];
                if (!await Fill(junk, ct).ConfigureAwait(false)) return null;
                _started = true;
            }
            while (true)
            {
                if (!await Fill(_head.AsMemory(0, 11), ct).ConfigureAwait(false)) return null;
                byte type = (byte)(_head[0] & 0x1F);
                int size = (_head[1] << 16) | (_head[2] << 8) | _head[3];
                uint ts = (uint)((_head[7] << 24) | (_head[4] << 16) | (_head[5] << 8) | _head[6]);
                if (size > MaxTagBody) throw new InvalidDataException("FLV tag too large");
                var body = new byte[size];
                if (!await Fill(body, ct).ConfigureAwait(false)) return null;
                if (!await Fill(_head.AsMemory(0, 4), ct).ConfigureAwait(false)) return null;   // PreviousTagSize
                if (type is TagAudio or TagVideo) return new FlvTag(type, ts, body);
            }
        }

        private async ValueTask<bool> Fill(Memory<byte> dst, CancellationToken ct)
        {
            int got = 0;
            while (got < dst.Length)
            {
                int n = await stream.ReadAsync(dst[got..], ct).ConfigureAwait(false);
                if (n == 0)
                {
                    if (got == 0) return false;
                    throw new EndOfStreamException("FLV stream ended mid-tag");
                }
                got += n;
            }
            return true;
        }
    }
}
