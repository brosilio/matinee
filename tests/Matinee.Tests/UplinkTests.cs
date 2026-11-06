using System.Diagnostics;
using Matinee.Media;
using static Matinee.Tests.EndToEndTests;

namespace Matinee.Tests;

/// <summary>Two instances in one process: home (RTMP ingest, uplink endpoint) and cloud (pulls from home, serves viewers).</summary>
internal static class UplinkTests
{
    private static Task<Rig> Home(string extra = "") => StartRawAsync($$"""
        ingest_listen 127.0.0.1:0;
        ingest_key "sekrit";
        uplink_key "homekey";
        {{extra}}
        """, "location = /uplink { matinee_uplink; }");

    private static Task<Rig> Cloud(Rig home, string key = "homekey", string extra = "") => StartRawAsync($$"""
        uplink_pull ws://127.0.0.1:{{home.HttpPort}}/uplink "{{key}}";
        password "pw";
        {{extra}}
        """);

    private static async Task Until(Func<bool> cond, int ms = 5000, string what = "condition")
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException($"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    public static void Register()
    {
        T.Add("uplink: the cloud plays what home ingests", async () =>
        {
            await using var home = await Home();
            await using var cloud = await Cloud(home);
            using var viewer = await Join(cloud, "alice", "tok-alice-123456789");

            using var pub = await RtmpPublisher.ConnectAsync(home.Ingest, "live", "sekrit");
            await Until(() => cloud.Engine.Room.Live, what: "the cloud to go live");
            await pub.SendVideoAsync(0, UnitTests.AvcSeqHeader());
            await pub.SendAudioAsync(0, UnitTests.AacSeqHeader());
            for (int i = 0; i < 4; i++)
            {
                await pub.SendVideoAsync((uint)(i * 33), UnitTests.AvcFrame(key: i == 0));
                await pub.SendAudioAsync((uint)(i * 23), UnitTests.AacFrame());
            }
            var kinds = new List<byte>();
            while (kinds.Count < 4) kinds.Add((await NextBinary(viewer))[0]);
            T.Eq(3, kinds[0]);            // video config first
            T.Eq(1, kinds[1]);            // then the keyframe
            T.True(kinds.Contains(4));    // audio config

            pub.Dispose();                // OBS stops: the cloud follows
            await Until(() => !cloud.Engine.Room.Live, what: "the cloud to end the stream");
        });

        T.Add("uplink: a wrong key gets nothing", async () =>
        {
            await using var home = await Home();
            await using var cloud = await Cloud(home, key: "nope");
            using var pub = await RtmpPublisher.ConnectAsync(home.Ingest, "live", "sekrit");
            await Task.Delay(1500);
            T.True(home.Engine.Room.Live);
            T.False(cloud.Engine.Room.Live);
        });

        T.Add("uplink: the cloud gets a re-encode made at home, x264 (real ffmpeg; skipped without it)", () => ReEncode(""));
        T.Add("uplink: the cloud gets a re-encode made at home, GPU/VAAPI (skipped without a render node)", () =>
            File.Exists("/dev/dri/renderD128") ? ReEncode("adapt_encoder vaapi;") : Skip("no /dev/dri/renderD128"));
    }

    private static Task Skip(string why)
    {
        Console.WriteLine($"       ({why}: skipped)");
        return Task.CompletedTask;
    }

    private static async Task ReEncode(string homeExtra)
    {
        {
            if (!HaveFfmpeg()) { await Skip("ffmpeg not found"); return; }
            await using var home = await Home(homeExtra);
            await using var cloud = await Cloud(home);

            // Real H.264 + AAC, published by ffmpeg like OBS would.
            using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-hide_banner", "-loglevel", "error", "-re",
                    "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=440",
                    "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "30", "-bf", "0",
                    "-c:a", "aac", "-t", "20",
                    "-f", "flv", $"rtmp://127.0.0.1:{home.Ingest.Port}/live/sekrit",
                },
                RedirectStandardError = true,
            })!;
            try
            {
                await Until(() => cloud.Engine.Room.Live && cloud.Engine.Room.Ring.LatestKey() >= 0, 10_000, "the source on the cloud");
                var room = cloud.Engine.Room;
                var r = room.RemoteEncoders!(300);                       // what the adaptation policy does
                await Until(() => r.Ready, 10_000, "home's re-encode to arrive");
                T.False(r.IsSource);
                // The re-encode lines up with the source on the cloud's clock (within the encoder's latency).
                var rk = r.Ring.Get(r.Ring.LatestKey())!;
                double sourceHead = room.Ring.HeadTs;
                T.True(Math.Abs(sourceHead - rk.Ts) < 3000);
                T.True(rk.Config!.Codec!.StartsWith("avc1", StringComparison.Ordinal));
                // ...with its audio too.
                await Until(() => HasAudio(r.Ring), 5000, "audio in the re-encode");
                await r.Encoder!.StopAsync();
            }
            finally
            {
                try { ffmpeg.Kill(); } catch (InvalidOperationException) { }
            }
        }
    }

    private static bool HasAudio(MediaRing ring)
    {
        for (long i = ring.Tail; i < ring.Head; i++) if (ring.Get(i) is { IsAudio: true }) return true;
        return false;
    }

    private static bool HaveFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "ffmpeg"))) return true;
        return false;
    }
}
