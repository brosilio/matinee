using System.Buffers;
using System.Text.Json;

namespace Matinee.Net;

/// <summary>
/// A client→server control message. Every message is a flat JSON object with a type in "t"; fields are a
/// fixed, small set, read without reflection.
///
/// <list type="bullet">
/// <item><c>{"t":"hello","nick":"..","pass":"..","tok":".."}</c>: first message. tok is a per-tab random token
/// so a reconnecting client can take over its own stale connection's nick.</item>
/// <item><c>{"t":"ping","c":clientMs,"a":lastMediaTs,"b":bufferedAheadMs}</c>: clock sync, ack and health.</item>
/// <item><c>{"t":"type","on":true}</c>: typing indicator.</item>
/// <item><c>{"t":"in","x":"current input","k":"word"|"edit"|"send"}</c>: chat input (see Floor).</item>
/// </list>
/// </summary>
public struct ClientMessage
{
    public string? T, Nick, Pass, Tok, X, K;
    public double C, A, B;
    public bool HasA, HasB, On;

    public static bool TryParse(ReadOnlySpan<byte> json, out ClientMessage m)
    {
        m = default;
        try
        {
            var r = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 8 });
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject) return m.T is not null;
                if (r.TokenType != JsonTokenType.PropertyName) return false;
                var name = r.ValueSpan;
                if (!r.Read()) return false;
                if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) { r.Skip(); continue; }
                if (name.SequenceEqual("t"u8)) m.T = Str(ref r);
                else if (name.SequenceEqual("nick"u8)) m.Nick = Str(ref r);
                else if (name.SequenceEqual("pass"u8)) m.Pass = Str(ref r);
                else if (name.SequenceEqual("tok"u8)) m.Tok = Str(ref r);
                else if (name.SequenceEqual("x"u8)) m.X = Str(ref r);
                else if (name.SequenceEqual("k"u8)) m.K = Str(ref r);
                else if (name.SequenceEqual("c"u8)) m.C = Num(ref r);
                else if (name.SequenceEqual("a"u8)) { m.A = Num(ref r); m.HasA = r.TokenType == JsonTokenType.Number; }
                else if (name.SequenceEqual("b"u8)) { m.B = Num(ref r); m.HasB = r.TokenType == JsonTokenType.Number; }
                else if (name.SequenceEqual("on"u8)) m.On = r.TokenType == JsonTokenType.True;
            }
            return false;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string? Str(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.String ? r.GetString() : null;
    private static double Num(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.Number && r.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;
}

/// <summary>Builds server→client text frames (complete WebSocket frames, ready to write).</summary>
public sealed class JsonFrame : IDisposable
{
    [ThreadStatic] private static ArrayBufferWriter<byte>? t_buf;
    private readonly ArrayBufferWriter<byte> _buf;
    public Utf8JsonWriter W { get; }

    private JsonFrame(string type)
    {
        _buf = t_buf ?? new ArrayBufferWriter<byte>(512);
        t_buf = null;
        _buf.ResetWrittenCount();
        W = new Utf8JsonWriter(_buf, new JsonWriterOptions { SkipValidation = true });
        W.WriteStartObject();
        W.WriteString("t"u8, type);
    }

    public static JsonFrame Start(string type) => new(type);

    /// <summary>Close the object and return the framed message.</summary>
    public byte[] Finish()
    {
        W.WriteEndObject();
        W.Flush();
        return WsFrame.Build(WsFrame.OpText, _buf.WrittenSpan);
    }

    public void Dispose()
    {
        W.Dispose();
        if (_buf.Capacity <= 64 * 1024) t_buf = _buf;
    }
}
