namespace Matinee.Media;

/// <summary>Turns codec configuration records into WebCodecs codec strings and audio parameters.</summary>
public static class Codecs
{
    /// <summary>H.264: "avc1.PPCCLL" from an AVCDecoderConfigurationRecord (avcC).</summary>
    public static string? AvcCodec(ReadOnlySpan<byte> avcC)
    {
        if (avcC.Length < 7 || avcC[0] != 1) return null;
        return $"avc1.{avcC[1]:x2}{avcC[2]:x2}{avcC[3]:x2}";
    }

    /// <summary>AV1: "av01.P.LLT.DD" from an AV1CodecConfigurationRecord (av1C).</summary>
    public static string? Av1Codec(ReadOnlySpan<byte> av1C)
    {
        if (av1C.Length < 4 || (av1C[0] & 0x7F) != 1) return null;   // marker=1, version=1
        int profile = av1C[1] >> 5;
        int level = av1C[1] & 0x1F;
        bool tier = (av1C[2] & 0x80) != 0;
        bool high = (av1C[2] & 0x40) != 0;
        bool twelve = (av1C[2] & 0x20) != 0;
        int depth = high ? (twelve ? 12 : 10) : 8;
        return $"av01.{profile}.{level:00}{(tier ? 'H' : 'M')}.{depth:00}";
    }

    private static readonly int[] AacRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>AAC: object type, sample rate and channels from an AudioSpecificConfig.</summary>
    public static bool ParseAsc(ReadOnlySpan<byte> asc, out string codec, out int sampleRate, out int channels)
    {
        codec = ""; sampleRate = 0; channels = 0;
        var br = new BitReader(asc);
        if (!br.TryRead(5, out int aot)) return false;
        if (aot == 31) { if (!br.TryRead(6, out int ext)) return false; aot = 32 + ext; }
        if (!br.TryRead(4, out int fi)) return false;
        if (fi == 15) { if (!br.TryRead(24, out sampleRate)) return false; }
        else if (fi < AacRates.Length) sampleRate = AacRates[fi];
        else return false;
        if (!br.TryRead(4, out channels)) return false;
        if (channels == 7) channels = 8;
        if (channels == 0) channels = 2;   // program config element: assume stereo
        codec = $"mp4a.40.{aot}";
        return true;
    }

    /// <summary>Opus: channels and input rate from an OpusHead identification header (decoding is always 48 kHz).</summary>
    public static bool ParseOpusHead(ReadOnlySpan<byte> head, out int channels)
    {
        channels = 0;
        if (head.Length < 19 || !head[..8].SequenceEqual("OpusHead"u8)) return false;
        channels = head[9];
        return channels > 0;
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private int _bit;

        public bool TryRead(int n, out int v)
        {
            v = 0;
            if (_bit + n > _d.Length * 8) return false;
            for (int i = 0; i < n; i++, _bit++)
                v = (v << 1) | ((_d[_bit >> 3] >> (7 - (_bit & 7))) & 1);
            return true;
        }
    }
}
