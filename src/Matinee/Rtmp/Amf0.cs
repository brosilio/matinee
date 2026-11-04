using System.Buffers.Binary;
using System.Text;

namespace Matinee.Rtmp;

/// <summary>
/// AMF0, the subset RTMP commands use. Values decode to: double, bool, string, null, <see cref="AmfObject"/>
/// (objects and ECMA arrays), or object?[] (strict arrays). Depth and sizes are bounded: this parses bytes from
/// whoever connects to the ingest port.
/// </summary>
public static class Amf0
{
    private const int MaxDepth = 8;
    private const int MaxItems = 1024;

    public static List<object?> DecodeAll(ReadOnlySpan<byte> data)
    {
        var list = new List<object?>();
        int pos = 0;
        while (pos < data.Length && list.Count < MaxItems) list.Add(Decode(data, ref pos, 0));
        return list;
    }

    public static object? Decode(ReadOnlySpan<byte> d, ref int pos, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("AMF0 nesting too deep");
        byte marker = Byte(d, ref pos);
        switch (marker)
        {
            case 0x00: return BinaryPrimitives.ReadDoubleBigEndian(Take(d, ref pos, 8));
            case 0x01: return Byte(d, ref pos) != 0;
            case 0x02: return ShortString(d, ref pos);
            case 0x03: return ObjectBody(d, ref pos, depth);
            case 0x05: return null;          // null
            case 0x06: return null;          // undefined
            case 0x08:                       // ECMA array: count (advisory) then object body
                Take(d, ref pos, 4);
                return ObjectBody(d, ref pos, depth);
            case 0x0A:
            {
                uint n = BinaryPrimitives.ReadUInt32BigEndian(Take(d, ref pos, 4));
                if (n > MaxItems) throw new FormatException("AMF0 array too long");
                var arr = new object?[n];
                for (int i = 0; i < n; i++) arr[i] = Decode(d, ref pos, depth + 1);
                return arr;
            }
            case 0x0B:                       // date: double + s16 timezone
                double ms = BinaryPrimitives.ReadDoubleBigEndian(Take(d, ref pos, 8));
                Take(d, ref pos, 2);
                return ms;
            case 0x0C:                       // long string
            {
                uint n = BinaryPrimitives.ReadUInt32BigEndian(Take(d, ref pos, 4));
                if (n > 1 << 20) throw new FormatException("AMF0 string too long");
                return Encoding.UTF8.GetString(Take(d, ref pos, (int)n));
            }
            default: throw new FormatException($"unsupported AMF0 marker 0x{marker:x2}");
        }
    }

    private static AmfObject ObjectBody(ReadOnlySpan<byte> d, ref int pos, int depth)
    {
        var o = new AmfObject();
        for (int i = 0; i <= MaxItems; i++)
        {
            string key = ShortString(d, ref pos);
            if (key.Length == 0 && pos < d.Length && d[pos] == 0x09) { pos++; return o; }
            o[key] = Decode(d, ref pos, depth + 1);
        }
        throw new FormatException("AMF0 object too big");
    }

    private static string ShortString(ReadOnlySpan<byte> d, ref int pos)
    {
        ushort n = BinaryPrimitives.ReadUInt16BigEndian(Take(d, ref pos, 2));
        return Encoding.UTF8.GetString(Take(d, ref pos, n));
    }

    private static byte Byte(ReadOnlySpan<byte> d, ref int pos)
    {
        if (pos >= d.Length) throw new FormatException("AMF0 truncated");
        return d[pos++];
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> d, ref int pos, int n)
    {
        if (n < 0 || pos + n > d.Length) throw new FormatException("AMF0 truncated");
        var s = d.Slice(pos, n);
        pos += n;
        return s;
    }

    // ---- Encoding ----

    public sealed class Writer
    {
        private byte[] _buf = new byte[256];
        private int _len;

        public ReadOnlySpan<byte> Written => _buf.AsSpan(0, _len);

        private Span<byte> Grow(int n)
        {
            if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
            var s = _buf.AsSpan(_len, n);
            _len += n;
            return s;
        }

        public Writer Number(double v) { var s = Grow(9); s[0] = 0; BinaryPrimitives.WriteDoubleBigEndian(s[1..], v); return this; }
        public Writer Bool(bool v) { var s = Grow(2); s[0] = 1; s[1] = v ? (byte)1 : (byte)0; return this; }
        public Writer Null() { Grow(1)[0] = 5; return this; }
        public Writer String(string v) { Grow(1)[0] = 2; Key(v); return this; }

        private void Key(string k)
        {
            var b = Encoding.UTF8.GetBytes(k);
            var s = Grow(2 + b.Length);
            BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)b.Length);
            b.CopyTo(s[2..]);
        }

        public Writer Object(params ReadOnlySpan<(string Key, object? Value)> props)
        {
            Grow(1)[0] = 3;
            foreach (var (k, v) in props)
            {
                Key(k);
                switch (v)
                {
                    case null: Null(); break;
                    case string s: String(s); break;
                    case bool b: Bool(b); break;
                    case double d: Number(d); break;
                    case int i: Number(i); break;
                    default: throw new ArgumentException("unsupported AMF0 value");
                }
            }
            var end = Grow(3);
            end[0] = 0; end[1] = 0; end[2] = 9;
            return this;
        }
    }
}

public sealed class AmfObject : Dictionary<string, object?>
{
    public AmfObject() : base(StringComparer.Ordinal) { }
    public string? Str(string key) => TryGetValue(key, out var v) ? v as string : null;
}
