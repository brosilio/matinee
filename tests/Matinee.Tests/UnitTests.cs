using System.Text;
using Matinee.Media;
using Matinee.Net;
using Matinee.Room;
using Matinee.Rtmp;

namespace Matinee.Tests;

internal static class UnitTests
{
    private sealed class Sink : IFloorSink
    {
        public readonly List<string> Lines = new();
        public readonly List<(string Nick, string Text)> Tooks = new();
        public int Changes;
        public void Line(ChatUser? user, string text, LineKind kind) => Lines.Add($"{(kind == LineKind.Clash ? "!" : "")}{user?.Nick ?? "*"}: {text}");
        public void Took(ChatUser user, string text) => Tooks.Add((user.Nick, text));
        public void StateChanged() => Changes++;
    }

    public static void Register()
    {
        // ---- Floor ----

        T.Add("floor: first completed word takes the floor, Enter commits", () =>
        {
            var s = new Sink(); var f = new Floor(s);
            var a = new ChatUser(1, "richard"); var b = new ChatUser(2, "james");
            f.Input(a, "hel", InputKind.Edit, 0);
            T.Eq(null, f.Owner);
            f.Input(a, "hello ", InputKind.Word, 0);
            T.Eq(a, f.Owner);
            f.Input(b, "hey ", InputKind.Word, 10);           // too late: james waits
            T.Eq(a, f.Owner);
            T.True(f.Wanting.Contains(b));
            f.Input(a, "hello wrold ", InputKind.Word, 20);
            f.Input(a, "hello world ", InputKind.Edit, 30);    // a correction to a word that's already out
            T.Eq("hello world ", f.LiveText);
            f.Input(a, "hello world ag", InputKind.Edit, 35);  // the word being typed stays private
            T.Eq("hello world ", f.LiveText);
            f.Input(a, "hello world", InputKind.Send, 40);
            T.Eq(null, f.Owner);
            T.Eq("richard: hello world", s.Lines[^1]);
            T.Eq(("richard", "hello world"), s.Tooks[^1]);
        });

        T.Add("floor: an unfinished word can't take the floor or contend for it", () =>
        {
            var s = new Sink(); var f = new Floor(s);
            var a = new ChatUser(1, "a"); var b = new ChatUser(2, "b");
            f.Input(a, "partialwordwithnospace", InputKind.Word, 0);   // a client that cheats on "word"
            T.Eq(null, f.Owner);
            f.Input(b, "real ", InputKind.Word, 1);
            T.Eq(b, f.Owner);
            T.Eq("real ", f.LiveText);
        });

        T.Add("floor: a refused Enter keeps the text; Enter interrupts once the speaker is quiet", () =>
        {
            var s = new Sink(); var f = new Floor(s);
            var a = new ChatUser(1, "a"); var b = new ChatUser(2, "b");
            f.Input(a, "talking ", InputKind.Word, 0);
            f.Input(b, "me too", InputKind.Send, 1);           // a is still talking: refused, kept
            T.Eq(0, s.Lines.Count);
            T.Eq(0, s.Tooks.Count);
            T.True(f.Wanting.Contains(b));
            f.Input(b, "me too", InputKind.Send, 1 + Floor.IdleMs);
            T.Eq("a: talking", s.Lines[^2]);                   // a's line is said as it stands...
            T.Eq("b: me too", s.Lines[^1]);                    // ...then b's
            T.False(f.Wanting.Contains(b));
        });

        T.Add("floor: a quiet speaker keeps the line until someone interrupts", () =>
        {
            var s = new Sink(); var f = new Floor(s);
            var a = new ChatUser(1, "a"); var b = new ChatUser(2, "b");
            f.Input(a, "i think teh ", InputKind.Word, 0);
            f.Tick(60_000);                                    // a minute of silence: still live, nothing said
            T.Eq(a, f.Owner);
            T.Eq(0, s.Lines.Count);
            f.Input(b, "anyway ", InputKind.Word, 60_000);     // b interrupts: a lives with the typo
            T.Eq("a: i think teh", s.Lines[^1]);
            T.Eq(("a", "i think teh"), s.Tooks[^1]);           // a's client strips that; an unsent word would stay
            T.Eq(b, f.Owner);
            T.Eq("anyway ", f.LiveText);
        });

        T.Add("floor: typing heartbeats keep a speaker mid-word from being interrupted", () =>
        {
            var s = new Sink(); var f = new Floor(s);
            var a = new ChatUser(1, "a"); var b = new ChatUser(2, "b");
            f.Input(a, "the word ", InputKind.Word, 0);
            f.SetTyping(a, true, 2000);                        // typing "antidisestablishmentarianism"
            f.SetTyping(a, true, 4000);
            f.Input(b, "hey ", InputKind.Word, 5000);          // only 1 s after a's last keystroke: no interruption
            T.Eq(a, f.Owner);
            T.True(f.Wanting.Contains(b));
        });

        T.Add("floor: words on air are corrected per character, and edits can't add words", () =>
        {
            var s = new Sink(); var f = new Floor(s);
            var a = new ChatUser(1, "a");
            f.Input(a, "hello wrold ", InputKind.Word, 0);
            f.Input(a, "hello wrol", InputKind.Edit, 1);       // backspacing into a word that's out: still out
            T.Eq("hello wrol", f.LiveText);
            f.Input(a, "hello world", InputKind.Edit, 2);
            T.Eq("hello world", f.LiveText);
            f.Input(a, "hello world again", InputKind.Edit, 3); // a new word only goes out on space
            T.Eq("hello world ", f.LiveText);
            f.Input(a, "hello world again ", InputKind.Word, 4);
            T.Eq("hello world again ", f.LiveText);
        });

        T.Add("floor: talking over someone for 8 words turns into a shouting match", () =>
        {
            var s = new Sink(); var f = new Floor(s);
            var a = new ChatUser(1, "a"); var b = new ChatUser(2, "b");
            f.Input(a, "so I was saying ", InputKind.Word, 0);
            string bt = "";
            for (int i = 1; i <= 7; i++)
            {
                bt += $"w{i} ";
                f.Input(b, bt, InputKind.Word, i);
            }
            T.False(f.Clash);
            bt += "w8 ";
            f.Input(b, bt, InputKind.Word, 8);
            T.True(f.Clash);
            T.Eq("a: so I was saying", s.Lines[^2]);
            T.Eq("!b: w1 w2 w3 w4 w5 w6 w7 w8", s.Lines[^1]);
            // Now each word is its own line, for both of them. Clients haven't stripped yet: input still has the
            // taken prefix, which the server normalizes away.
            f.Input(b, bt + "nine ", InputKind.Word, 9);
            T.Eq("!b: nine", s.Lines[^1]);
            f.Input(a, "so I was saying listen ", InputKind.Word, 10);
            T.Eq("!a: listen", s.Lines[^1]);
            f.Tick(10 + Floor.IdleMs);
            T.False(f.Clash);
            T.Eq(null, f.Owner);
        });

        T.Add("floor: owner leaving commits their line", () =>
        {
            var s = new Sink(); var f = new Floor(s);
            var a = new ChatUser(1, "a");
            f.Input(a, "bye now ", InputKind.Word, 0);
            f.Leave(a);
            T.Eq("a: bye now", s.Lines[^1]);
            T.Eq(null, f.Owner);
        });

        T.Add("floor: normalize strips taken prefixes until the client applies them", () =>
        {
            var u = new ChatUser(1, "u");
            u.PendingTook.Add("hello world");
            T.Eq("more", Floor.Normalize(u, "hello world more"));   // client hasn't applied yet
            T.Eq(1, u.PendingTook.Count);
            T.Eq("more", Floor.Normalize(u, "more"));               // applied
            T.Eq(0, u.PendingTook.Count);
        });

        T.Add("floor: complete words", () =>
        {
            T.Eq(0, Floor.CompleteWords("hello"));
            T.Eq(1, Floor.CompleteWords("hello "));
            T.Eq(2, Floor.CompleteWords("  a  b c"));
            T.Eq("a b ", Floor.CompletePart("a b cd"));
        });

        // ---- Shared sync timeline ----

        T.Add("sync: slow viewer pauses, recovers, and room catches up at 1.05x", () =>
        {
            var tl = new SyncTimeline();
            tl.Reset(10_000, 1000);
            var paused = tl.Tick(10_000, 1000, 5000, false, false, double.NaN, 7, "slow");
            T.Eq(SyncEventKind.Pause, paused.Kind);
            T.Eq(0.0, tl.Rate);
            T.Eq(9000.0, tl.Playhead(11_000));

            var resumed = tl.Tick(11_000, 1000, 5000, true, true, 2500, 0, null);
            T.Eq(SyncEventKind.Resume, resumed.Kind);
            T.Eq(1.05, tl.Rate);

            var caught = tl.Tick(31_000, 1000, 5000, false, false, double.NaN, 0, null);
            T.Eq(SyncEventKind.CaughtUp, caught.Kind);
            T.Eq(1.0, tl.Rate);
            T.Eq(30_000.0, tl.Playhead(31_000));
        });

        T.Add("sync: max delay reconnects a viewer that cannot catch up", () =>
        {
            var tl = new SyncTimeline();
            tl.Reset(10_000, 1000);
            var paused = tl.Tick(10_000, 1000, 2000, false, false, double.NaN, 9, "stuck");
            T.Eq(SyncEventKind.Pause, paused.Kind);
            T.Eq(1000.0, paused.PauseBudgetMs);

            var dropped = tl.Tick(11_001, 1000, 2000, true, true, 100, 9, "stuck");
            T.Eq(SyncEventKind.DropViewer, dropped.Kind);
            T.True(dropped.TimelineChanged);
            T.Eq(1.05, tl.Rate);
        });

        // ---- WebSocket ----

        T.Add("ws: accept key (RFC 6455 example)", () =>
        {
            T.Eq("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WsHandshake.Accept("dGhlIHNhbXBsZSBub25jZQ=="));
            T.Eq(null, WsHandshake.Accept("short"));
        });

        T.Add("ws: reads masked fragmented text and control frames", async () =>
        {
            var ms = new MemoryStream();
            WriteClientFrame(ms, 0x01, fin: false, "hel"u8);
            WriteClientFrame(ms, 0x09, fin: true, "p"u8);   // ping between fragments
            WriteClientFrame(ms, 0x00, fin: true, "lo"u8);
            ms.Position = 0;
            var r = new WsReader(ms);
            var m1 = await r.ReadAsync(default);
            T.True(m1!.Value.IsPing);
            var m2 = await r.ReadAsync(default);
            T.Eq("hello", Encoding.UTF8.GetString(m2!.Value.Payload));
            T.Eq(null, await r.ReadAsync(default));
        });

        T.Add("ws: rejects unmasked and oversized frames", async () =>
        {
            var ms = new MemoryStream([0x81, 0x02, (byte)'h', (byte)'i']);
            await Throws<WsProtocolException>(() => new WsReader(ms).ReadAsync(default).AsTask());
            var big = new MemoryStream();
            WriteClientFrame(big, 0x02, true, new byte[100]);
            big.Position = 0;
            await Throws<WsProtocolException>(() => new WsReader(big, maxMessage: 50).ReadAsync(default).AsTask());
        });

        T.Add("ws: frame header lengths", () =>
        {
            Span<byte> h = stackalloc byte[10];
            T.Eq(2, WsFrame.WriteHeader(h, WsFrame.OpBinary, 125));
            T.Eq(4, WsFrame.WriteHeader(h, WsFrame.OpBinary, 126));
            T.Eq(10, WsFrame.WriteHeader(h, WsFrame.OpBinary, 70000));
            T.Eq((byte)127, h[1]);
        });

        // ---- JSON ----

        T.Add("json: client messages", () =>
        {
            T.True(ClientMessage.TryParse("""{"t":"ping","c":12.5,"a":1000,"extra":{"x":[1]}}"""u8, out var m));
            T.Eq("ping", m.T); T.Eq(12.5, m.C); T.True(m.HasA); T.Eq(1000.0, m.A); T.False(m.HasB);
            T.True(ClientMessage.TryParse("""{"t":"in","x":"hi there","k":"word"}"""u8, out m));
            T.Eq("hi there", m.X);
            T.False(ClientMessage.TryParse("""{"x":1}"""u8, out _));
            T.False(ClientMessage.TryParse("""[1,2]"""u8, out _));
            T.False(ClientMessage.TryParse("""{"t":"#"""u8, out _));
        });

        // ---- AMF0 / codecs ----

        T.Add("amf0: round trip", () =>
        {
            var w = new Amf0.Writer().String("connect").Number(1).Object(("app", "live"), ("n", 2.0), ("b", true)).Null();
            var v = Amf0.DecodeAll(w.Written);
            T.Eq("connect", (string)v[0]!);
            T.Eq(1.0, (double)v[1]!);
            var o = (AmfObject)v[2]!;
            T.Eq("live", o.Str("app"));
            T.Eq(true, (bool)o["b"]!);
            T.Eq(null, v[3]);
        });

        T.Add("amf0: truncated and hostile input throws FormatException", () =>
        {
            ThrowsSync<FormatException>(() => Amf0.DecodeAll([0x02, 0x00, 0x10, 0x41]));
            var deep = new byte[64];
            Array.Fill(deep, (byte)0x0A);   // nested strict arrays with garbage lengths
            ThrowsSync<FormatException>(() => Amf0.DecodeAll(deep));
            ThrowsSync<FormatException>(() => Amf0.DecodeAll([0x42]));
        });

        T.Add("proxy: FLV tags preserve type, extended timestamp, body and size", () =>
        {
            byte[] tag = FfmpegRelay.BuildTag(9, 0x12345678, [1, 2, 3]);
            T.Eq(18, tag.Length);
            T.Eq((byte)9, tag[0]);
            T.Eq((byte)3, tag[3]);
            T.Eq((byte)0x34, tag[4]);
            T.Eq((byte)0x56, tag[5]);
            T.Eq((byte)0x78, tag[6]);
            T.Eq((byte)0x12, tag[7]);
            T.True(tag.AsSpan(11, 3).SequenceEqual(new byte[] { 1, 2, 3 }));
            T.True(tag.AsSpan(14, 4).SequenceEqual(new byte[] { 0, 0, 0, 14 }));
        });

        T.Add("codecs: avc, av1, aac, opus", () =>
        {
            T.Eq("avc1.42c01f", Codecs.AvcCodec([1, 0x42, 0xC0, 0x1F, 0xFF, 0xE1, 0]));
            T.Eq("av01.0.08M.08", Codecs.Av1Codec([0x81, 0x08, 0x0C, 0x00]));
            T.True(Codecs.ParseAsc([0x12, 0x10], out var c, out var rate, out var ch));   // AAC-LC 44.1k stereo
            T.Eq("mp4a.40.2", c); T.Eq(44100, rate); T.Eq(2, ch);
            var head = new byte[19];
            "OpusHead"u8.CopyTo(head);
            head[8] = 1; head[9] = 2;
            T.True(Codecs.ParseOpusHead(head, out ch));
            T.Eq(2, ch);
        });

        // ---- Ring ----

        T.Add("ring: keeps a keyframe and trims by age", () =>
        {
            var ring = new MediaRing(capacityPow2: 64, retainMs: 1000);
            for (int i = 0; i < 100; i++) ring.Append(MediaFrame.Create(FrameKind.Video, i % 30 == 0, i * 100, [1, 2, 3]));
            var key = ring.LatestKey();
            T.True(key >= 0);
            T.True(ring.Get(key)!.IsKey);
            T.True(ring.Tail <= key);
            T.True(ring.Head - ring.Tail <= 64);
            T.True(ring.HeadTs - ring.Get(ring.Tail)!.Ts <= 1000 || ring.Tail == key);
            T.Eq(-1L, ring.KeyAtOrAfter(ring.Head));
            T.Eq(key, ring.KeyAtOrBefore(ring.HeadTs));
        });

        T.Add("ingest: legacy AVC tags become frames on the server clock", () =>
        {
            var frames = new List<MediaFrame>();
            var ing = new IngestSession(frames.Add, new NullLog());
            ing.OnVideo(0, AvcSeqHeader());
            ing.OnVideo(0, AvcFrame(key: true));
            ing.OnVideo(33, AvcFrame(key: false));
            T.Eq(3, frames.Count);
            T.Eq(FrameKind.VideoConfig, frames[0].Kind);
            T.Eq("avc1.42c01f", frames[0].Codec);
            T.True(frames[1].IsKey);
            T.True(frames[2].Ts > frames[1].Ts);
            T.True(ReferenceEquals(frames[0], frames[2].Config));
            // Wire format: header, kind, flags, ts.
            var f = frames[1];
            int hdr = f.Wire.Length - f.PayloadLength - MediaFrame.HeaderSize;
            T.Eq((byte)FrameKind.Video, f.Wire[hdr]);
            T.Eq((byte)1, f.Wire[hdr + 1]);
        });
    }

    internal static byte[] AvcSeqHeader() => [0x17, 0, 0, 0, 0, 1, 0x42, 0xC0, 0x1F, 0xFF, 0xE1, 0x00, 0x04, 0x67, 0x42, 0xC0, 0x1F, 0x01, 0x00, 0x02, 0x68, 0xCE];
    internal static byte[] AvcFrame(bool key) => [key ? (byte)0x17 : (byte)0x27, 1, 0, 0, 0, 0, 0, 0, 2, 0x65, 0x88];
    internal static byte[] AacSeqHeader() => [0xAF, 0, 0x12, 0x10];
    internal static byte[] AacFrame() => [0xAF, 1, 0x21, 0x00, 0x49];

    private static void WriteClientFrame(Stream s, byte op, bool fin, ReadOnlySpan<byte> payload)
    {
        s.WriteByte((byte)((fin ? 0x80 : 0) | op));
        s.WriteByte((byte)(0x80 | payload.Length));
        byte[] mask = [1, 2, 3, 4];
        s.Write(mask);
        for (int i = 0; i < payload.Length; i++) s.WriteByte((byte)(payload[i] ^ mask[i & 3]));
    }

    private static async Task Throws<TE>(Func<Task> f) where TE : Exception
    {
        try { await f(); }
        catch (TE) { return; }
        throw new Exception($"expected {typeof(TE).Name}");
    }

    private static void ThrowsSync<TE>(Action f) where TE : Exception
    {
        try { f(); }
        catch (TE) { return; }
        throw new Exception($"expected {typeof(TE).Name}");
    }
}

internal sealed class NullLog : Httpgrl.ILogger
{
    public bool IsEnabled(Httpgrl.LogLevel level) => false;
    public void Write(Httpgrl.LogLevel level, string message, ReadOnlySpan<Httpgrl.LogField> fields) { }
}
