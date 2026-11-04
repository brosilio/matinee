using System.Diagnostics;
using Httpgrl;

namespace Matinee.Media;

/// <summary>
/// A live re-encode of the source for viewers on slow links: ffmpeg reads the source ring as FLV on stdin and writes
/// a lower-bitrate FLV on stdout, which is parsed into its own ring.
///
/// Timestamps survive the round trip: frames go in stamped relative to a base on the server clock, ffmpeg keeps them
/// (<c>-copyts</c>), and they come out mapped back onto the same clock. So the re-encode lines up frame for frame
/// with the source; it just arrives a little later (the encoder's latency comes out of the viewers' delay budget).
///
/// If ffmpeg can't keep up with real time, its input skips ahead to the newest keyframe rather than falling behind.
/// </summary>
internal sealed class Transcoder : IEncoder
{
    private const double MaxInputLagMs = 2000;
    private const int MaxLoggedLines = 20;

    private readonly Process _process;
    private readonly MediaRing _source;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _input, _output, _stderr;
    private readonly string _label;
    private double _base = double.NaN;
    private int _failed;

    public bool Failed => Volatile.Read(ref _failed) != 0;
    public int Pid { get; }

    private Transcoder(Process process, MediaRing source, MediaRing target, string label, Action wake, ILogger log)
    {
        _process = process;
        _source = source;
        _log = log;
        _label = label;
        Pid = process.Id;
        long startKey = source.LatestKey();
        var first = startKey >= 0 ? source.Get(startKey) : null;
        _base = first is null ? Clock.NowMs : Math.Floor(first.Dts);
        double b = _base;
        var ingest = new IngestSession(f => { target.Append(f); wake(); }, log, fixedStamp: src => b + src);
        _input = PumpInputAsync(startKey);
        _output = ReadOutputAsync(ingest);
        _stderr = ReadStderrAsync();
    }

    /// <summary>Start a re-encode of <paramref name="source"/> at <paramref name="kbps"/> video bitrate.</summary>
    public static Rendition Start(MediaRing source, int kbps, AdaptSettings s, Action wake, ILogger log)
    {
        int height = HeightFor(kbps);
        string label = $"{height}p · {kbps} kbit/s";
        var psi = new ProcessStartInfo
        {
            FileName = s.Ffmpeg,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in Args(kbps, height, s)) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi };
        try
        {
            if (!p.Start()) throw new InvalidOperationException("ffmpeg did not start");
        }
        catch
        {
            p.Dispose();
            throw;
        }
        var ring = new MediaRing(retainMs: source.RetainMs);
        var t = new Transcoder(p, source, ring, label, wake, log);
        log.Info("adapt: transcoder started", new("rendition", label), new("pid", (long)t.Pid));
        return new Rendition(ring, kbps, height, label) { Encoder = t };
    }

    internal static IEnumerable<string> Args(int kbps, int height, AdaptSettings s)
    {
        string bufsize = $"{Math.Max(kbps / 2, 100)}k";
        List<string> a =
        [
            "-hide_banner", "-loglevel", "warning", "-nostdin",
            "-fflags", "+nobuffer", "-flags", "low_delay", "-probesize", "256k", "-analyzeduration", "200000",
        ];
        if (s.Encoder == AdaptEncoder.Vaapi) a.AddRange(["-vaapi_device", s.VaapiDevice]);
        a.AddRange(["-f", "flv", "-i", "pipe:0", "-map", "0:v:0", "-map", "0:a:0?"]);
        if (s.Encoder == AdaptEncoder.Vaapi)
        {
            // Decode on the CPU, scale and encode on the GPU.
            a.AddRange([
                "-vf", $"format=nv12,hwupload,scale_vaapi=w=-2:h=min(ih\\,{height})",
                "-c:v", "h264_vaapi", "-rc_mode", "CBR", "-bf", "0",
            ]);
        }
        else
        {
            a.AddRange([
                "-vf", $"scale=-2:min(ih\\,{height})",
                "-c:v", "libx264", "-preset", s.Preset, "-tune", "zerolatency", "-profile:v", "main", "-bf", "0",
            ]);
        }
        a.AddRange([
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", bufsize,
            "-force_key_frames", "expr:gte(t,n_forced*1)",
            "-c:a", "aac", "-b:a", $"{s.AudioKbps}k", "-ar", "48000", "-ac", "2",
            "-copyts", "-fps_mode", "passthrough", "-muxdelay", "0", "-muxpreload", "0",
            "-flvflags", "no_duration_filesize",
        ]);
        a.AddRange(s.ExtraArgs);
        a.AddRange(["-f", "flv", "pipe:1"]);
        return a;
    }

    /// <summary>A sensible picture height for a bitrate.</summary>
    internal static int HeightFor(int kbps) => kbps switch
    {
        >= 1800 => 720,
        >= 1000 => 540,
        >= 550 => 360,
        _ => 240,
    };

    private async Task PumpInputAsync(long cursor)
    {
        var ct = _stop.Token;
        try
        {
            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(Flv.Header.ToArray(), ct).ConfigureAwait(false);
            MediaFrame? sentVideoCfg = null, sentAudioCfg = null;
            while (!ct.IsCancellationRequested)
            {
                var next = _source.NextAppend;
                if (cursor < 0 || cursor < _source.Tail)
                {
                    cursor = _source.LatestKey();
                    if (cursor < 0) { await next.WaitAsync(ct).ConfigureAwait(false); continue; }
                }
                var f = _source.Get(cursor);
                if (f is null) { await next.WaitAsync(ct).ConfigureAwait(false); continue; }

                // ffmpeg slower than real time: skip ahead rather than fall behind.
                if (_source.HeadTs - f.Ts > MaxInputLagMs)
                {
                    long k = _source.LatestKey();
                    if (k > cursor)
                    {
                        _log.Warn("adapt: transcoder can't keep up; skipping ahead", new LogField("rendition", _label));
                        cursor = k;
                        continue;
                    }
                }
                cursor++;
                if (f.Kind is FrameKind.VideoConfig or FrameKind.AudioConfig) continue;   // sent before the frames that need them
                if (f.Dts < _base) continue;

                var cfg = f.Config;
                if (cfg is not null && !ReferenceEquals(cfg, f.IsVideo ? sentVideoCfg : sentAudioCfg))
                {
                    var cfgTag = Flv.FromFrame(WithDts(cfg, f.Dts), _base);
                    if (cfgTag is not null) await stdin.WriteAsync(cfgTag, ct).ConfigureAwait(false);
                    if (f.IsVideo) sentVideoCfg = cfg; else sentAudioCfg = cfg;
                }
                var tag = Flv.FromFrame(f, _base);
                if (tag is not null) await stdin.WriteAsync(tag, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Fail("input", e.Message);
        }
    }

    /// <summary>Config frames carry their own (older) timestamp; send them stamped with the frame that needs them.</summary>
    private static MediaFrame WithDts(MediaFrame cfg, double dts)
    {
        var p = cfg.Payload;
        int n = p[0];
        string codec = System.Text.Encoding.ASCII.GetString(p.Slice(1, n));
        int rate = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p[(1 + n)..]);
        int ch = p[1 + n + 4];
        return MediaFrame.CreateConfig(cfg.Kind, codec, rate, ch, cfg.Description, dts);
    }

    private async Task ReadOutputAsync(IngestSession ingest)
    {
        var ct = _stop.Token;
        try
        {
            var reader = new Flv.Reader(_process.StandardOutput.BaseStream);
            while (await reader.NextAsync(ct).ConfigureAwait(false) is { } tag)
            {
                if (tag.Type == Flv.TagVideo) ingest.OnVideo(tag.Timestamp, tag.Body);
                else ingest.OnAudio(tag.Timestamp, tag.Body);
            }
            if (!ct.IsCancellationRequested) Fail("output", "ffmpeg exited");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException or InvalidOperationException)
        {
            Fail("output", e.Message);
        }
    }

    private async Task ReadStderrAsync()
    {
        int logged = 0;
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (logged++ < MaxLoggedLines) _log.Warn("adapt: ffmpeg", new("rendition", _label), new("line", line));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void Fail(string where, string why)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0 || _stop.IsCancellationRequested) return;
        _log.Error("adapt: transcoder failed", new("rendition", _label), new("where", where), new("error", why));
    }

    public async Task StopAsync()
    {
        Volatile.Write(ref _failed, 1);
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        try { _process.StandardInput.Close(); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
        try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await _process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
        foreach (var t in new[] { _input, _output, _stderr })
        {
            try { await t.ConfigureAwait(false); } catch (Exception) { }
        }
        _log.Info("adapt: transcoder stopped", new("rendition", _label), new("pid", (long)Pid));
        _process.Dispose();
        _stop.Dispose();
    }
}
