using Httpgrl;

namespace Matinee.Media;

/// <summary>
/// One publisher's stream: turns FLV video/audio tag bodies (as carried by RTMP, legacy or Enhanced RTMP) into
/// <see cref="MediaFrame"/>s on the server clock and hands them to the room.
///
/// <b>Timestamps.</b> A frame's source timestamp plus an offset gives its server-clock time. The offset tracks the
/// latest-arriving frames: a frame that arrives after its stamped time pushes the offset up (so frames are never
/// stamped in the past at ingest, which would eat the viewers' delay budget); otherwise the offset decays slowly
/// back down, so one network spike between OBS and us doesn't add latency forever. A jump of more than 2 s
/// (a new publisher, a timestamp reset) re-anchors. Audio and video share the offset, which keeps them in sync.
/// </summary>
public sealed class IngestSession(Action<MediaFrame> append, ILogger log)
{
    private const double ReanchorMs = 2000;
    private const double DecayPerFrameMs = 0.1;

    private double _offset = double.NaN;
    private long _srcHigh;                      // unwrapped 32-bit RTMP timestamps
    private uint _lastSrc;
    private bool _haveSrc;
    private double _lastVideo = double.NegativeInfinity, _lastAudio = double.NegativeInfinity;

    public MediaFrame? VideoConfig { get; private set; }
    public MediaFrame? AudioConfig { get; private set; }
    public long Frames { get; private set; }
    public long Bytes { get; private set; }

    private double Unwrap(uint ts)
    {
        if (_haveSrc)
        {
            if (ts < _lastSrc && _lastSrc - ts > 0x8000_0000u) _srcHigh += 1L << 32;
            else if (ts > _lastSrc && ts - _lastSrc > 0x8000_0000u && _srcHigh > 0) return _srcHigh - (1L << 32) + ts; // late frame from before a wrap
        }
        _lastSrc = ts;
        _haveSrc = true;
        return _srcHigh + ts;
    }

    /// <summary>Server-clock time for a source decode timestamp (ms).</summary>
    private double Stamp(double srcMs)
    {
        double now = Clock.NowMs;
        if (double.IsNaN(_offset) || Math.Abs(now - (srcMs + _offset)) > ReanchorMs) _offset = now - srcMs;
        else if (srcMs + _offset < now) _offset = now - srcMs;
        else _offset -= DecayPerFrameMs;
        return srcMs + _offset;
    }

    public void OnVideo(uint rtmpTs, ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) return;
        byte b0 = body[0];
        int frameType;
        int packetType;
        string codec;                 // "avc1" | "av01"
        int cts = 0;
        ReadOnlySpan<byte> data;

        if ((b0 & 0x80) != 0)
        {
            // Enhanced RTMP: [isEx:1][frameType:3][packetType:4][fourcc:4]...
            if (body.Length < 5) return;
            frameType = (b0 >> 4) & 7;
            packetType = b0 & 0x0F;
            var fourcc = body.Slice(1, 4);
            if (fourcc.SequenceEqual("avc1"u8)) codec = "avc1";
            else if (fourcc.SequenceEqual("av01"u8)) codec = "av01";
            else { Unsupported("video", System.Text.Encoding.ASCII.GetString(fourcc)); return; }
            data = body[5..];
            switch (packetType)
            {
                case 0: break;                                   // SequenceStart
                case 1:                                          // CodedFrames
                    if (codec == "avc1")
                    {
                        if (data.Length < 3) return;
                        cts = Si24(data);
                        data = data[3..];
                    }
                    packetType = 1;
                    break;
                case 3: packetType = 1; break;                   // CodedFramesX: no composition time
                case 2: return;                                  // SequenceEnd
                default: return;                                 // metadata, multitrack, ...
            }
        }
        else
        {
            frameType = b0 >> 4;
            int codecId = b0 & 0x0F;
            if (codecId != 7) { Unsupported("video", $"flv codec id {codecId}"); return; }
            if (body.Length < 5) return;
            codec = "avc1";
            packetType = body[1];
            cts = Si24(body[2..]);
            data = body[5..];
            if (packetType == 2) return;                         // end of sequence
            if (packetType is not (0 or 1)) return;
        }
        if (frameType == 5) return;                              // video info / command frame

        double dts = Unwrap(rtmpTs);
        if (packetType == 0)
        {
            string? cs = codec == "avc1" ? Codecs.AvcCodec(data) : Codecs.Av1Codec(data);
            if (cs is null) { log.Warn("ingest: bad video config record", new LogField("codec", codec)); return; }
            var cfg = MediaFrame.CreateConfig(FrameKind.VideoConfig, cs, 0, 0, data, Stamp(dts));
            if (VideoConfig is null || VideoConfig.Codec != cs || !SamePayload(VideoConfig, cfg))
            {
                VideoConfig = cfg;
                append(cfg);
                log.Info("ingest: video config", new LogField("codec", cs));
            }
            return;
        }
        if (VideoConfig is null || data.IsEmpty) return;
        double ts = Stamp(dts) + cts;
        if (ts <= _lastVideo) ts = _lastVideo + 0.001;
        if (cts == 0) _lastVideo = ts;   // with B-frames (cts != 0) presentation order isn't monotonic
        var f = MediaFrame.Create(FrameKind.Video, frameType == 1, ts, data, VideoConfig);
        Frames++;
        Bytes += data.Length;
        append(f);
    }

    public void OnAudio(uint rtmpTs, ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) return;
        int fmt = body[0] >> 4;
        bool isOpus;
        int packetType;
        ReadOnlySpan<byte> data;
        if (fmt == 10)
        {
            isOpus = false;
            packetType = body[1];
            data = body[2..];
        }
        else if (fmt == 9)
        {
            // Enhanced RTMP audio: [soundFormat=9:4][packetType:4][fourcc:4]...
            if (body.Length < 5) return;
            packetType = body[0] & 0x0F;
            var fourcc = body.Slice(1, 4);
            if (fourcc.SequenceEqual("Opus"u8)) isOpus = true;
            else if (fourcc.SequenceEqual("mp4a"u8)) isOpus = false;
            else { Unsupported("audio", System.Text.Encoding.ASCII.GetString(fourcc)); return; }
            data = body[5..];
            if (packetType is not (0 or 1)) return;
        }
        else { Unsupported("audio", $"flv sound format {fmt}"); return; }

        double dts = Unwrap(rtmpTs);
        if (packetType == 0)
        {
            string codec;
            int rate, ch;
            if (isOpus)
            {
                if (!Codecs.ParseOpusHead(data, out ch)) { log.Warn("ingest: bad OpusHead"); return; }
                codec = "opus"; rate = 48000;
            }
            else if (!Codecs.ParseAsc(data, out codec, out rate, out ch)) { log.Warn("ingest: bad AudioSpecificConfig"); return; }
            var cfg = MediaFrame.CreateConfig(FrameKind.AudioConfig, codec, rate, ch, data, Stamp(dts));
            if (AudioConfig is null || !SamePayload(AudioConfig, cfg))
            {
                AudioConfig = cfg;
                append(cfg);
                log.Info("ingest: audio config", new("codec", codec), new("rate", (long)rate), new("channels", (long)ch));
            }
            return;
        }
        if (AudioConfig is null || data.IsEmpty) return;
        double ts = Stamp(dts);
        if (ts <= _lastAudio) ts = _lastAudio + 0.001;
        _lastAudio = ts;
        Frames++;
        Bytes += data.Length;
        append(MediaFrame.Create(FrameKind.Audio, true, ts, data, AudioConfig));
    }

    private static bool SamePayload(MediaFrame a, MediaFrame b)
    {
        int ha = a.Wire.Length - a.PayloadLength, hb = b.Wire.Length - b.PayloadLength;
        return a.Wire.AsSpan(ha).SequenceEqual(b.Wire.AsSpan(hb));
    }

    private static int Si24(ReadOnlySpan<byte> b)
    {
        int v = (b[0] << 16) | (b[1] << 8) | b[2];
        return (v & 0x800000) != 0 ? v - 0x1000000 : v;
    }

    private readonly HashSet<string> _warned = new();
    private void Unsupported(string what, string codec)
    {
        if (_warned.Add(what + codec)) log.Warn("ingest: unsupported codec, dropping", new("track", what), new("codec", codec));
    }
}
