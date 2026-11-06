using System.Diagnostics;
using Matinee.Media;

namespace Matinee.Tests;

internal static class HlsTests
{
    internal static bool HaveTool(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return true;
        return false;
    }

    internal static (int Code, string Out, string Err) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, err);
    }

    /// <summary>Real H.264 + AAC from ffmpeg, through our FLV reader and ingest: the frames matinee would hold.</summary>
    internal static async Task<List<MediaFrame>> RealFrames(double seconds)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-bf", "0", "-c:a", "aac", "-ac", "2", "-f", "flv", "pipe:1",
        }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var frames = new List<MediaFrame>();
        var ingest = new IngestSession(frames.Add, new NullLog(), fixedStamp: src => 10_000 + src);
        var reader = new Flv.Reader(p.StandardOutput.BaseStream);
        while (await reader.NextAsync(default) is { } tag)
        {
            if (tag.Type == Flv.TagVideo) ingest.OnVideo(tag.Timestamp, tag.Body);
            else ingest.OnAudio(tag.Timestamp, tag.Body);
        }
        await p.WaitForExitAsync();
        return frames;
    }

    private const string Token = "vrc-movie-night-0123456789";

    private static async Task<string> Get(int port, string path, int expect = 200)
    {
        using var http = new HttpClient();
        var r = await http.GetAsync($"http://127.0.0.1:{port}{path}");
        T.Eq(expect, (int)r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    private static List<(long Seq, double Dur)> Segments(string playlist)
    {
        var list = new List<(long, double)>();
        double dur = 0;
        foreach (var line in playlist.Split('\n'))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                dur = double.Parse(line[8..].TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture);
            else if (line.EndsWith(".ts", StringComparison.Ordinal))
                list.Add((long.Parse(line[(line.IndexOf('/') + 1)..^3], System.Globalization.CultureInfo.InvariantCulture), dur));
        }
        return list;
    }

    public static void Register()
    {
        T.Add("hls: real players read it; variants line up; the token guards it (real ffmpeg; skipped without it)", async () =>
        {
            if (!HaveTool("ffmpeg") || !HaveTool("ffprobe")) { Console.WriteLine("       (ffmpeg not found: skipped)"); return; }
            await using var rig = await EndToEndTests.StartRawAsync($$"""
                ingest_listen 127.0.0.1:0;
                ingest_key "sekrit";
                hls on;
                hls_token {{Token}};
                hls_low 300k;
                hls_window 6;
                hls_start 3s;
                """, "location /hls/ { matinee_hls; }");
            using var pub = Process.Start(new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-hide_banner", "-loglevel", "error", "-re",
                    "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-f", "lavfi", "-i", "sine=frequency=440",
                    "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "30", "-bf", "0",
                    "-c:a", "aac", "-t", "25", "-f", "flv", $"rtmp://127.0.0.1:{rig.Ingest.Port}/live/sekrit",
                },
                RedirectStandardError = true,
            })!;
            try
            {
                int port = rig.HttpPort;
                string url = $"/hls/{Token}/index.m3u8";
                await Get(port, $"/hls/not-the-token-0123456789/index.m3u8", expect: 404);
                var master = await Get(port, url);             // also starts the low variant
                T.True(master.Contains("src.m3u8"));
                T.True(master.Contains("low.m3u8"));

                // Wait for both variants to have a few segments.
                var sw = Stopwatch.StartNew();
                List<(long Seq, double Dur)> src = [], low = [];
                while (sw.ElapsedMilliseconds < 15_000)
                {
                    src = Segments(await Get(port, $"/hls/{Token}/src.m3u8"));
                    low = Segments(await Get(port, $"/hls/{Token}/low.m3u8"));
                    if (src.Count >= 4 && low.Count >= 3) break;
                    await Task.Delay(250);
                }
                T.True(src.Count >= 4);
                T.True(low.Count >= 3);
                foreach (var (seq, dur) in src) T.True(dur is > 0.85 and < 1.2);        // cut at every 1 s keyframe
                // Same segment numbers cover the same stretch of time in both variants.
                foreach (var (seq, dur) in low)
                {
                    int i = src.FindIndex(x => x.Seq == seq);
                    if (i >= 0) T.True(Math.Abs(src[i].Dur - dur) < 0.05);
                }
                T.True(low.Exists(l => src.Exists(x => x.Seq == l.Seq)));

                var playlist = await Get(port, $"/hls/{Token}/src.m3u8");
                // Aimed at "3 s behind now": the offset from the playlist's end is 3 s less the time since it ended.
                var start = System.Text.RegularExpressions.Regex.Match(playlist, @"#EXT-X-START:TIME-OFFSET=-([0-9.]+),PRECISE=YES");
                T.True(start.Success);
                double off = double.Parse(start.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                T.True(off is >= 1.0 and <= 3.5);   // a little over 3 when the newest segment ends after "now" (ingest stamps a little ahead)
                T.True(playlist.Contains("#EXT-X-PROGRAM-DATE-TIME:"));

                // A real HLS client: probes the master, decodes 3 s of each variant without a single error.
                string full = $"http://127.0.0.1:{port}{url}";
                var (_, probe, _) = Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", full);
                T.True(probe.Contains("h264"));
                T.True(probe.Contains("aac"));
                foreach (var v in new[] { "src", "low" })
                {
                    var (code, _, err) = Run("ffmpeg", "-v", "error", "-i", $"http://127.0.0.1:{port}/hls/{Token}/{v}.m3u8", "-t", "3", "-f", "null", "-");
                    T.Eq($"{v}: 0", $"{v}: {code}");
                    T.Eq($"{v}: ", $"{v}: {err.Trim()}");
                }
            }
            finally
            {
                try { pub.Kill(); } catch (InvalidOperationException) { }
            }
        });

        T.Add("ts: PAT/PMT CRC is MPEG-2's", () =>
        {
            // CRC-32/MPEG-2 check value for "123456789".
            T.Eq(0x0376E6E7u, TsMuxer.Crc32("123456789"u8));
        });

        T.Add("ts: real H.264 + AAC muxes into a stream ffmpeg decodes cleanly", async () =>
        {
            if (!HaveTool("ffmpeg") || !HaveTool("ffprobe")) { Console.WriteLine("       (ffmpeg not found: skipped)"); return; }
            var frames = await RealFrames(3);
            var mux = new TsMuxer();
            var ms = new MemoryStream();
            mux.WriteHeader(ms, audio: true);
            int written = 0;
            foreach (var f in frames) if (mux.Write(ms, f)) written++;
            T.Eq(0, (int)(ms.Length % 188));
            T.True(written > 150);
            var file = Path.Combine(Path.GetTempPath(), $"matinee-ts-{Environment.ProcessId}.ts");
            File.WriteAllBytes(file, ms.ToArray());
            try
            {
                var (code, _, err) = Run("ffmpeg", "-v", "error", "-i", file, "-f", "null", "-");
                T.Eq(0, code);
                T.Eq("", err.Trim());                                       // no decode errors at all
                var (_, probe, _) = Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_name:format=duration", "-of", "csv=p=0", file);
                T.True(probe.Contains("h264"));
                T.True(probe.Contains("aac"));
                var dur = double.Parse(probe.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1], System.Globalization.CultureInfo.InvariantCulture);
                T.True(Math.Abs(dur - 3) < 0.2);
            }
            finally { File.Delete(file); }
        });
    }
}
