using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Httpgrl;
using Httpgrl.Core.Hosting;
using Httpgrl.Core.Logging;
using Matinee.Hosting;
using Matinee.Room;
using Matinee.Rtmp;

namespace Matinee.Tests;

/// <summary>The whole thing in-process: httpgrl + engine, an RTMP publisher, WebSocket viewers.</summary>
internal static class EndToEndTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required Engine Engine;
        public required HttpServer Server;
        public required JsonLogger Log;
        public required string Dir;
        public int HttpPort => ((IPEndPoint)Server.EndPoints[0]).Port;
        public IPEndPoint Ingest => Engine.IngestEndPoint!;

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Server.StopAsync(1000);
            Log.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    private static async Task<Rig> StartAsync(string extra = "")
    {
        var dir = Directory.CreateTempSubdirectory("matinee-test-").FullName;
        var log = new JsonLogger(Environment.GetEnvironmentVariable("TEST_LOG") == "1" ? Console.OpenStandardOutput() : Stream.Null, LogLevel.Debug);
        var engine = new Engine(new StreamSettings(), log);
        StreamModule? built = null;
        IModule[] Modules()
        {
            built = new StreamModule(engine);
            return [built, .. Httpgrl.Modules.StaticFiles.StaticFiles.Modules()];
        }
        var conf = $$"""
            matinee {
                ingest_listen 127.0.0.1:0;
                ingest_key "sekrit";
                password "pw";
                {{extra}}
            }
            http { server { listen 127.0.0.1:0; location = /ws { matinee_ws; } } }
            """;
        var server = ServerBuilder.LoadText(conf, dir, Modules, log);
        await engine.ApplyAsync(built!.Settings);
        server.Start();
        return new Rig { Engine = engine, Server = server, Log = log, Dir = dir };
    }

    public static void Register()
    {
        T.Add("e2e: wrong password is refused", async () =>
        {
            await using var rig = await StartAsync();
            using var ws = await Connect(rig);
            await SendJson(ws, $$"""{"t":"hello","nick":"bob","pass":"nope","tok":"0123456789abcdef0"}""");
            var m = await NextJson(ws, "error");
            T.Eq("auth", m.GetProperty("code").GetString());
        });

        T.Add("e2e: duplicate nick refused, same token takes over", async () =>
        {
            await using var rig = await StartAsync();
            using var a = await Join(rig, "bob", "tok-aaaaaaaaaaaaaaaa");
            using var b = await Connect(rig);
            await SendJson(b, """{"t":"hello","nick":"BOB","pass":"pw","tok":"tok-bbbbbbbbbbbbbbbb"}""");
            T.Eq("nick", (await NextJson(b, "error")).GetProperty("code").GetString());
            using var c = await Join(rig, "bob", "tok-aaaaaaaaaaaaaaaa");   // reconnect from the same tab
            T.Eq(1, rig.Engine.Room.ViewerCount);
        });

        T.Add("e2e: publisher to viewer, with clock sync and chat", async () =>
        {
            await using var rig = await StartAsync();
            using var viewer = await Join(rig, "alice", "tok-alice-123456789");

            // Pings are answered with the server clock.
            await SendJson(viewer, """{"t":"ping","c":42}""");
            var pong = await NextJson(viewer, "pong");
            T.Eq(42.0, pong.GetProperty("c").GetDouble());
            T.True(pong.GetProperty("s").GetDouble() > 0);

            using var pub = await RtmpPublisher.ConnectAsync(rig.Ingest, "live", "sekrit");
            await NextJson(viewer, "status", m => m.GetProperty("live").GetBoolean());

            await pub.SendVideoAsync(0, UnitTests.AvcSeqHeader());
            await pub.SendAudioAsync(0, UnitTests.AacSeqHeader());
            await pub.SendVideoAsync(0, UnitTests.AvcFrame(key: true));
            await pub.SendAudioAsync(10, UnitTests.AacFrame());
            await pub.SendVideoAsync(33, UnitTests.AvcFrame(key: false));

            // Config before media, keyframe first.
            var kinds = new List<byte>();
            while (kinds.Count < 5)
            {
                var bin = await NextBinary(viewer);
                kinds.Add(bin[0]);
            }
            T.Eq(3, kinds[0]);                     // video config
            T.Eq(1, kinds[1]);                     // keyframe
            T.True(kinds.Contains(4));             // audio config before audio
            T.True(kinds.IndexOf(4) < kinds.IndexOf(2));

            // Chat: completing a word takes the floor; Enter commits it to history.
            await SendJson(viewer, """{"t":"in","k":"word","x":"hello "}""");
            var floor = await NextJson(viewer, "floor", m => m.GetProperty("o").ValueKind == JsonValueKind.String);
            T.Eq("alice", floor.GetProperty("o").GetString());
            await SendJson(viewer, """{"t":"in","k":"send","x":"hello world"}""");
            var line = await NextJson(viewer, "line", m => m.GetProperty("n").ValueKind == JsonValueKind.String);
            T.Eq("hello world", line.GetProperty("x").GetString());
            T.Eq("hello world", (await NextJson(viewer, "took")).GetProperty("x").GetString());
        });

        T.Add("e2e: a late joiner starts at the newest keyframe", async () =>
        {
            await using var rig = await StartAsync();
            using var pub = await RtmpPublisher.ConnectAsync(rig.Ingest, "live", "sekrit");
            await pub.SendVideoAsync(0, UnitTests.AvcSeqHeader());
            for (int i = 0; i < 90; i++) await pub.SendVideoAsync((uint)(i * 33), UnitTests.AvcFrame(key: i % 30 == 0));
            await Task.Delay(200);
            using var viewer = await Join(rig, "late", "tok-late-1234567890");
            var cfg = await NextBinary(viewer);
            T.Eq(3, cfg[0]);
            var first = await NextBinary(viewer);
            T.Eq(1, first[0]);
            T.Eq(1, first[1] & 1);                 // keyframe
        });

        T.Add("e2e: wrong stream key is refused", async () =>
        {
            await using var rig = await StartAsync();
            await Throws(() => RtmpPublisher.ConnectAsync(rig.Ingest, "live", "wrong"));
            T.False(rig.Engine.Room.Live);
        });

        T.Add("e2e: sync mode pauses and resumes the shared timeline", async () =>
        {
            await using var rig = await StartAsync("mode sync; delay 1s; max_delay 5s;");
            using var pub = await RtmpPublisher.ConnectAsync(rig.Ingest, "live", "sekrit");
            using var viewer = await Join(rig, "slow", "tok-slow-123456789");
            var joined = System.Diagnostics.Stopwatch.StartNew();

            // Just joined: an empty buffer is filling up, not falling behind. It must not pause the room.
            await SendJson(viewer, """{"t":"ping","c":1,"a":1,"b":100}""");
            await NextJson(viewer, "pong");
            await Task.Delay(600);                                   // a few room ticks
            await SendJson(viewer, """{"t":"ping","c":1,"a":1,"b":100}""");
            await NextJson(viewer, "pong");
            T.Eq(1.0, rig.Engine.Room.Rate);

            // Past the grace period, a viewer that's still starved pauses everyone.
            await Task.Delay(TimeSpan.FromMilliseconds(Viewer.SyncGraceMs + 300) - joined.Elapsed);
            await SendJson(viewer, """{"t":"ping","c":1,"a":1,"b":100}""");
            var paused = await NextJson(viewer, "tl", m => m.GetProperty("r").GetDouble() == 0);
            T.Eq(0.0, paused.GetProperty("r").GetDouble());

            await SendJson(viewer, """{"t":"ping","c":2,"a":2,"b":2500}""");
            var resumed = await NextJson(viewer, "tl", m => m.GetProperty("r").GetDouble() > 1);
            T.Eq(1.05, resumed.GetProperty("r").GetDouble());
        });

        T.Add("e2e: shutdown closes and awaits an active publisher", async () =>
        {
            await using var rig = await StartAsync();
            using var pub = await RtmpPublisher.ConnectAsync(rig.Ingest, "live", "sekrit");
            T.True(rig.Engine.Room.Live);

            var shutdown = rig.Engine.DisposeAsync().AsTask();
            if (await Task.WhenAny(shutdown, Task.Delay(2000)) != shutdown)
                throw new TimeoutException("engine did not stop with an active publisher");
            await shutdown;
            T.False(rig.Engine.Room.Live);
        });
    }

    // ---- WebSocket client helpers ----

    private static async Task<ClientWebSocket> Connect(Rig rig)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.HttpPort}/ws"), CancellationToken.None);
        return ws;
    }

    private static async Task<ClientWebSocket> Join(Rig rig, string nick, string tok)
    {
        var ws = await Connect(rig);
        await SendJson(ws, $$"""{"t":"hello","nick":"{{nick}}","pass":"pw","tok":"{{tok}}"}""");
        await NextJson(ws, "welcome");
        return ws;
    }

    private static Task SendJson(ClientWebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<(WebSocketMessageType Type, byte[] Data)> Receive(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(5000);
        var buf = new byte[1 << 20];
        int n = 0;
        while (true)
        {
            var r = await ws.ReceiveAsync(buf.AsMemory(n), cts.Token);
            if (r.MessageType == WebSocketMessageType.Close) throw new Exception("closed: " + ws.CloseStatusDescription);
            n += r.Count;
            if (r.EndOfMessage) return (r.MessageType, buf[..n]);
        }
    }

    private static async Task<JsonElement> NextJson(ClientWebSocket ws, string type, Func<JsonElement, bool>? where = null)
    {
        while (true)
        {
            var (t, data) = await Receive(ws);
            if (t != WebSocketMessageType.Text) continue;
            var doc = JsonDocument.Parse(data).RootElement;
            if (doc.GetProperty("t").GetString() == type && (where is null || where(doc))) return doc;
        }
    }

    private static async Task<byte[]> NextBinary(ClientWebSocket ws)
    {
        while (true)
        {
            var (t, data) = await Receive(ws);
            if (t == WebSocketMessageType.Binary) return data;
        }
    }

    private static async Task Throws(Func<Task> f)
    {
        try { await f(); }
        catch (Exception) { return; }
        throw new Exception("expected an exception");
    }
}

/// <summary>A minimal RTMP publisher (what OBS does), for tests.</summary>
internal sealed class RtmpPublisher : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _s;

    private RtmpPublisher(TcpClient tcp) { _tcp = tcp; _s = tcp.GetStream(); }

    public static async Task<RtmpPublisher> ConnectAsync(IPEndPoint ep, string app, string key)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(ep);
        var p = new RtmpPublisher(tcp);
        try
        {
            await p.HandshakeAsync();
            await p.CommandAsync(3, 0, new Amf0.Writer().String("connect").Number(1).Object(("app", app), ("type", "nonprivate")));
            await p.ExpectAsync("_result");
            await p.CommandAsync(3, 0, new Amf0.Writer().String("createStream").Number(2).Null());
            await p.ExpectAsync("_result");
            await p.CommandAsync(4, 1, new Amf0.Writer().String("publish").Number(3).Null().String(key).String("live"));
            var status = await p.ExpectAsync("onStatus");
            var info = (AmfObject)status[3]!;
            if (info.Str("code") != "NetStream.Publish.Start") throw new Exception("publish refused: " + info.Str("code"));
            return p;
        }
        catch
        {
            p.Dispose();
            throw;
        }
    }

    private async Task HandshakeAsync()
    {
        var c01 = new byte[1537];
        c01[0] = 3;
        await _s.WriteAsync(c01);
        var s012 = new byte[1 + 1536 * 2];
        await _s.ReadExactlyAsync(s012);
        await _s.WriteAsync(s012.AsMemory(1, 1536));   // C2 echoes S1
    }

    public Task SendVideoAsync(uint ts, byte[] body) => SendAsync(6, 9, 1, ts, body);
    public Task SendAudioAsync(uint ts, byte[] body) => SendAsync(4, 8, 1, ts, body);

    private Task CommandAsync(int csid, uint streamId, Amf0.Writer w) => SendAsync(csid, 20, streamId, 0, w.Written.ToArray());

    /// <summary>One message as fmt-0 chunk plus fmt-3 continuations, default chunk size 128.</summary>
    private async Task SendAsync(int csid, byte type, uint streamId, uint ts, byte[] body)
    {
        var ms = new MemoryStream();
        ms.WriteByte((byte)csid);
        Span<byte> h = stackalloc byte[11];
        h[0] = (byte)(ts >> 16); h[1] = (byte)(ts >> 8); h[2] = (byte)ts;
        h[3] = (byte)(body.Length >> 16); h[4] = (byte)(body.Length >> 8); h[5] = (byte)body.Length;
        h[6] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(h[7..], streamId);
        ms.Write(h);
        for (int o = 0; o < body.Length; o += 128)
        {
            if (o > 0) ms.WriteByte((byte)(0xC0 | csid));
            ms.Write(body, o, Math.Min(128, body.Length - o));
        }
        await _s.WriteAsync(ms.ToArray());
    }

    /// <summary>Read server messages until a command named <paramref name="name"/> (server uses chunk size 4096 after connect).</summary>
    private async Task<List<object?>> ExpectAsync(string name)
    {
        using var cts = new CancellationTokenSource(5000);
        while (true)
        {
            var b0 = new byte[1];
            await _s.ReadExactlyAsync(b0, cts.Token);
            int fmt = b0[0] >> 6;
            if (fmt != 0 && fmt != 3) throw new Exception("unexpected chunk fmt " + fmt);
            if (fmt == 3) throw new Exception("unexpected continuation");
            var h = new byte[11];
            await _s.ReadExactlyAsync(h, cts.Token);
            int len = (h[3] << 16) | (h[4] << 8) | h[5];
            byte type = h[6];
            var body = new byte[len];
            int got = 0;
            while (got < len)
            {
                if (got > 0) await _s.ReadExactlyAsync(b0, cts.Token);   // fmt-3 header
                int n = Math.Min(4096, len - got);
                await _s.ReadExactlyAsync(body.AsMemory(got, n), cts.Token);
                got += n;
            }
            if (type != 20) continue;
            var v = Amf0.DecodeAll(body);
            if (v.Count > 0 && v[0] as string == name) return v;
        }
    }

    public void Dispose()
    {
        _s.Dispose();
        _tcp.Dispose();
    }
}
