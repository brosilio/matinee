using System.Buffers.Binary;

namespace Matinee.Media;

/// <summary>
/// An MPEG-TS writer for HLS segments: H.264 and AAC, the two codecs every HLS player (and VRChat on PC and Quest)
/// can play.
///
/// Frames come from the ring in the forms the web player uses (AVCC length-prefixed NAL units, raw AAC), so they're
/// converted here: H.264 to Annex B (start codes, an access unit delimiter, and SPS/PPS before every keyframe so each
/// segment decodes on its own), AAC behind ADTS headers. Timestamps are the server clock in 90 kHz units.
/// Every segment starts with PAT and PMT; continuity counters run on across segments, as players expect.
/// </summary>
public sealed class TsMuxer
{
    private const int PacketSize = 188;
    private const ushort PmtPid = 0x1000, VideoPid = 0x100, AudioPid = 0x101;

    private readonly byte[] _pkt = new byte[PacketSize];
    private byte _ccPat, _ccPmt, _ccVideo, _ccAudio;
    private long _lastVideoDts = -1, _lastAudioPts = -1;   // 90 kHz; a track's timestamps must strictly increase

    // H.264 parameters from the avcC record.
    private MediaFrame? _videoCfg;
    private int _nalLengthSize = 4;
    private byte[] _spsPps = [];
    // ADTS header fields from the AudioSpecificConfig.
    private MediaFrame? _audioCfg;
    private int _aacProfile, _aacFreqIndex, _aacChannels;
    private bool _audioOk;

    public bool HasAudio { get; private set; }

    /// <summary>The codecs this muxer can carry.</summary>
    public static bool Supports(MediaFrame? cfg) => cfg?.Codec is { } c && (c.StartsWith("avc1", StringComparison.Ordinal) || c.StartsWith("mp4a.40.", StringComparison.Ordinal));

    /// <summary>Start a segment: PAT and PMT.</summary>
    public void WriteHeader(Stream o, bool audio)
    {
        HasAudio = audio;
        // PAT: program 1 -> PMT.
        Span<byte> pat = stackalloc byte[16];   // 12 bytes of section + 4 of CRC
        pat[0] = 0x00;                       // table_id
        pat[1] = 0xB0; pat[2] = 13;          // section_syntax_indicator, length (9 + 4 CRC)
        pat[3] = 0x00; pat[4] = 0x01;        // transport_stream_id
        pat[5] = 0xC1;                       // version 0, current
        pat[6] = 0; pat[7] = 0;              // section numbers
        pat[8] = 0x00; pat[9] = 0x01;        // program 1
        pat[10] = (byte)(0xE0 | PmtPid >> 8); pat[11] = PmtPid & 0xFF;
        WriteSection(o, 0, ref _ccPat, pat);

        // PMT: H.264 video (carries the PCR) and, if present, AAC/ADTS audio.
        int n = audio ? 2 : 1;
        Span<byte> pmt = stackalloc byte[12 + 5 * n + 4];
        pmt[0] = 0x02;
        int len = 9 + 5 * n + 4;
        pmt[1] = (byte)(0xB0 | len >> 8); pmt[2] = (byte)len;
        pmt[3] = 0x00; pmt[4] = 0x01;        // program 1
        pmt[5] = 0xC1; pmt[6] = 0; pmt[7] = 0;
        pmt[8] = (byte)(0xE0 | VideoPid >> 8); pmt[9] = VideoPid & 0xFF;   // PCR PID
        pmt[10] = 0xF0; pmt[11] = 0;         // program_info_length 0
        int p = 12;
        Stream(pmt, ref p, 0x1B, VideoPid);
        if (audio) Stream(pmt, ref p, 0x0F, AudioPid);
        WriteSection(o, PmtPid, ref _ccPmt, pmt);

        static void Stream(Span<byte> s, ref int p, byte type, ushort pid)
        {
            s[p] = type;
            s[p + 1] = (byte)(0xE0 | pid >> 8); s[p + 2] = (byte)pid;
            s[p + 3] = 0xF0; s[p + 4] = 0;   // ES_info_length 0
            p += 5;
        }
    }

    /// <summary>Write one media frame. Returns false for a frame that can't be carried (unknown codec, no config).</summary>
    public bool Write(Stream o, MediaFrame f)
    {
        if (f.IsVideo) return WriteVideo(o, f);
        if (f.IsAudio && HasAudio) return WriteAudio(o, f);
        return false;
    }

    private bool WriteVideo(Stream o, MediaFrame f)
    {
        if (!ReferenceEquals(f.Config, _videoCfg) && !SetVideoConfig(f.Config)) return false;
        if (!Advances(ref _lastVideoDts, Ticks(f.Dts))) return false;
        var data = f.Payload;
        // Annex B access unit: AUD, SPS/PPS on keyframes, then each NAL behind a start code.
        int size = 6 + (f.IsKey ? _spsPps.Length : 0);
        for (int i = 0; i + _nalLengthSize <= data.Length;)
        {
            int n = NalLength(data[i..]);
            i += _nalLengthSize;
            if (n <= 0 || i + n > data.Length) return false;
            size += 4 + n;
            i += n;
        }
        var es = new byte[size];
        int w = 0;
        es[w++] = 0; es[w++] = 0; es[w++] = 0; es[w++] = 1; es[w++] = 0x09; es[w++] = 0xF0;   // AUD, any slice type
        if (f.IsKey) { _spsPps.CopyTo(es, w); w += _spsPps.Length; }
        for (int i = 0; i + _nalLengthSize <= data.Length;)
        {
            int n = NalLength(data[i..]);
            i += _nalLengthSize;
            es[w++] = 0; es[w++] = 0; es[w++] = 0; es[w++] = 1;
            data.Slice(i, n).CopyTo(es.AsSpan(w));
            w += n;
            i += n;
        }
        WritePes(o, VideoPid, ref _ccVideo, 0xE0, Ticks(f.Ts), Ticks(f.Dts), es, pcr: true, random: f.IsKey);
        return true;
    }

    private int NalLength(ReadOnlySpan<byte> b) => _nalLengthSize switch
    {
        4 => (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b), int.MaxValue),
        2 => BinaryPrimitives.ReadUInt16BigEndian(b),
        1 => b[0],
        _ => (b[0] << 16) | (b[1] << 8) | b[2],
    };

    private bool SetVideoConfig(MediaFrame? cfg)
    {
        if (cfg?.Codec is not { } c || !c.StartsWith("avc1", StringComparison.Ordinal)) return false;
        var r = cfg.Description;   // AVCDecoderConfigurationRecord
        if (r.Length < 7) return false;
        _nalLengthSize = (r[4] & 3) + 1;
        var ms = new MemoryStream();
        int p = 5;
        int nsps = r[p++] & 0x1F;
        for (int i = 0; i < nsps; i++) if (!CopyNal(r, ref p, ms)) return false;
        if (p >= r.Length) return false;
        int npps = r[p++];
        for (int i = 0; i < npps; i++) if (!CopyNal(r, ref p, ms)) return false;
        _spsPps = ms.ToArray();
        _videoCfg = cfg;
        return true;

        static bool CopyNal(ReadOnlySpan<byte> r, ref int p, MemoryStream ms)
        {
            if (p + 2 > r.Length) return false;
            int n = BinaryPrimitives.ReadUInt16BigEndian(r[p..]);
            p += 2;
            if (p + n > r.Length) return false;
            ms.Write([0, 0, 0, 1]);
            ms.Write(r.Slice(p, n));
            p += n;
            return true;
        }
    }

    private bool WriteAudio(Stream o, MediaFrame f)
    {
        if (!ReferenceEquals(f.Config, _audioCfg))
        {
            _audioCfg = f.Config;
            _audioOk = SetAudioConfig(f.Config);
        }
        if (!_audioOk) return false;
        // Re-encoders occasionally put two audio frames on the same tick; players reject that, so drop the extra.
        if (!Advances(ref _lastAudioPts, Ticks(f.Ts))) return false;
        var data = f.Payload;
        int len = 7 + data.Length;
        if (len > 0x1FFF) return false;
        var es = new byte[len];
        // ADTS, no CRC.
        es[0] = 0xFF;
        es[1] = 0xF1;                                                       // MPEG-4, layer 0, no CRC
        es[2] = (byte)((_aacProfile & 3) << 6 | (_aacFreqIndex & 0xF) << 2 | (_aacChannels >> 2 & 1));
        es[3] = (byte)((_aacChannels & 3) << 6 | (len >> 11 & 3));
        es[4] = (byte)(len >> 3);
        es[5] = (byte)((len & 7) << 5 | 0x1F);                              // buffer fullness 0x7FF (VBR)
        es[6] = 0xFC;                                                       // ... and one raw data block
        data.CopyTo(es.AsSpan(7));
        WritePes(o, AudioPid, ref _ccAudio, 0xC0, Ticks(f.Ts), -1, es, pcr: false, random: true);
        return true;
    }

    private bool SetAudioConfig(MediaFrame? cfg)
    {
        if (cfg?.Codec is not { } c || !c.StartsWith("mp4a.40.", StringComparison.Ordinal)) return false;
        var asc = cfg.Description;
        if (asc.Length < 2) return false;
        int aot = asc[0] >> 3;
        int freq = (asc[0] & 7) << 1 | asc[1] >> 7;
        int ch = (asc[1] >> 3) & 0xF;
        if (aot is < 1 or > 4 || freq > 12 || ch is 0 or > 7) return false;   // ADTS can only say AAC Main/LC/SSR/LTP
        _aacProfile = aot - 1;
        _aacFreqIndex = freq;
        _aacChannels = ch;
        return true;
    }

    /// <summary>True (and remembered) if <paramref name="t"/> is after <paramref name="last"/> on the wrapping 33-bit clock.</summary>
    private static bool Advances(ref long last, long t)
    {
        if (last >= 0)
        {
            long d = (t - last) & 0x1_FFFF_FFFF;
            if (d == 0 || d > 0x1_0000_0000) return false;   // same tick, or behind (more than half the clock away)
        }
        last = t;
        return true;
    }

    /// <summary>Server-clock milliseconds to the 33-bit 90 kHz clock.</summary>
    internal static long Ticks(double ms) => (long)Math.Round(ms * 90) & 0x1_FFFF_FFFF;

    private void WritePes(Stream o, ushort pid, ref byte cc, byte streamId, long pts, long dts, byte[] es, bool pcr, bool random)
    {
        bool hasDts = dts >= 0 && dts != pts;
        int hdrData = hasDts ? 10 : 5;
        var head = new byte[9 + hdrData];
        head[0] = 0; head[1] = 0; head[2] = 1; head[3] = streamId;
        int pesLen = 3 + hdrData + es.Length;
        if (pesLen > 0xFFFF || streamId == 0xE0) pesLen = 0;              // video: unbounded is allowed
        head[4] = (byte)(pesLen >> 8); head[5] = (byte)pesLen;
        head[6] = 0x80;                                                    // marker bits
        head[7] = (byte)(hasDts ? 0xC0 : 0x80);
        head[8] = (byte)hdrData;
        Timestamp(head.AsSpan(9), hasDts ? (byte)0x30 : (byte)0x20, pts);
        if (hasDts) Timestamp(head.AsSpan(14), 0x10, dts);

        int total = head.Length + es.Length;
        int done = 0;
        bool first = true;
        while (done < total)
        {
            var p = _pkt.AsSpan();
            p.Clear();
            p[0] = 0x47;
            p[1] = (byte)((first ? 0x40 : 0) | pid >> 8);
            p[2] = (byte)pid;

            // Adaptation field: flags (+ PCR) on the first packet of a keyframe / video PES, then stuffing if the
            // payload doesn't fill the packet. afBytes counts the whole field, its length byte included.
            bool flags = first && (pcr || random);
            int afBytes = flags ? 2 + (pcr ? 6 : 0) : 0;
            int left = total - done;
            int room = 184 - afBytes;
            int stuffing = 0;
            if (left < room)
            {
                stuffing = room - left;
                if (afBytes == 0)
                {
                    // A field only for stuffing: just its length byte (stuffing 1), or length + empty flags + 0xFFs.
                    afBytes = stuffing;
                    stuffing = Math.Max(0, stuffing - 2);
                    flags = afBytes >= 2;
                }
                else afBytes += stuffing;
                room = left;
            }
            int w = 4;
            if (afBytes > 0)
            {
                p[3] = (byte)(0x30 | cc);
                p[4] = (byte)(afBytes - 1);
                w = 5;
                if (afBytes >= 2)
                {
                    p[w++] = (byte)((first && random ? 0x40 : 0) | (first && pcr ? 0x10 : 0));
                    if (first && pcr)
                    {
                        long b = (dts >= 0 ? dts : pts) - 9000 & 0x1_FFFF_FFFF;   // PCR a little behind DTS
                        p[w++] = (byte)(b >> 25); p[w++] = (byte)(b >> 17); p[w++] = (byte)(b >> 9); p[w++] = (byte)(b >> 1);
                        p[w++] = (byte)((b & 1) << 7 | 0x7E); p[w++] = 0;
                    }
                    while (w < 4 + afBytes) p[w++] = 0xFF;
                }
            }
            else p[3] = (byte)(0x10 | cc);
            cc = (byte)((cc + 1) & 0xF);

            int n = Math.Min(room, left);
            for (int i = 0; i < n; i++)
            {
                int k = done + i;
                p[w + i] = k < head.Length ? head[k] : es[k - head.Length];
            }
            done += n;
            o.Write(_pkt);
            first = false;
        }
    }

    private static void Timestamp(Span<byte> d, byte prefix, long t)
    {
        d[0] = (byte)(prefix | (t >> 29 & 0x0E) | 1);
        d[1] = (byte)(t >> 22);
        d[2] = (byte)((t >> 14 & 0xFE) | 1);
        d[3] = (byte)(t >> 7);
        d[4] = (byte)((t << 1 & 0xFE) | 1);
    }

    private void WriteSection(Stream o, ushort pid, ref byte cc, ReadOnlySpan<byte> section)
    {
        var p = _pkt.AsSpan();
        p.Fill(0xFF);
        p[0] = 0x47;
        p[1] = (byte)(0x40 | pid >> 8);
        p[2] = (byte)pid;
        p[3] = (byte)(0x10 | cc);
        cc = (byte)((cc + 1) & 0xF);
        p[4] = 0;                                     // pointer field
        section.CopyTo(p[5..]);
        uint crc = Crc32(section[..^4]);
        BinaryPrimitives.WriteUInt32BigEndian(p[(5 + section.Length - 4)..], crc);
        o.Write(_pkt);
    }

    /// <summary>MPEG-2 CRC-32 (polynomial 0x04C11DB7, not reflected).</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 24;
            for (int i = 0; i < 8; i++) crc = (crc & 0x80000000) != 0 ? crc << 1 ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }
}
