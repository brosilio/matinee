using Matinee.Media;
using Matinee.Room;

namespace Matinee.Tests;

/// <summary>The adaptation policy, driven tick by tick with simulated viewers (no sockets, no ffmpeg).</summary>
internal static class AdaptiveTests
{
    private sealed class FakeViewer(int id, string nick, Rendition source) : IAdaptViewer
    {
        public int Id { get; } = id;
        public string Nick { get; } = nick;
        public Rendition Rendition { get; private set; } = source;
        public Rendition? PendingRendition => null;
        public long VideoSent { get; private set; }
        public long VideoDropped { get; private set; }
        public long AckedBytes { get; private set; }
        /// <summary>What the link can carry (kbit/s), and the share of video lost on the source.</summary>
        public double LinkKbps = 10_000;
        public void SwitchTo(Rendition r) => Rendition = r;

        /// <summary>One second of watching: drops happen when the rendition is bigger than the link.</summary>
        public void Second(int sourceKbps)
        {
            int kbps = Rendition.IsSource ? sourceKbps : Rendition.Kbps + 64;
            double fits = Math.Min(1, LinkKbps / kbps);
            int frames = 30;
            int sent = (int)Math.Round(frames * fits);
            VideoSent += sent;
            VideoDropped += frames - sent;
            AckedBytes += (long)(Math.Min(kbps, LinkKbps) * 1000 / 8);
        }
    }

    private sealed class Rig
    {
        public readonly Rendition Source = Rendition.Source(new MediaRing());
        public readonly List<Rendition> Started = new(), Stopped = new();
        public readonly Adaptive A;
        public readonly List<FakeViewer> Viewers = new();
        public double Now = Clock.NowMs;
        public long SourceBytes;
        public AdaptSettings S = new() { Enabled = true };
        public const int SourceKbps = 2500;

        public Rig()
        {
            A = new Adaptive(Source, kbps =>
            {
                var ring = new MediaRing();
                ring.Append(MediaFrame.Create(FrameKind.Video, true, Clock.NowMs, [1]));   // ready: has a keyframe
                var r = new Rendition(ring, kbps, Transcoder.HeightFor(kbps), $"{kbps}k");
                Started.Add(r);
                return r;
            }, r => Stopped.Add(r), new NullLog());
        }

        public FakeViewer Add(string nick, double linkKbps)
        {
            var v = new FakeViewer(Viewers.Count + 1, nick, Source) { LinkKbps = linkKbps };
            Viewers.Add(v);
            return v;
        }

        public void Run(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                Now += 1000;
                SourceBytes += SourceKbps * 1000 / 8;
                foreach (var v in Viewers) v.Second(SourceKbps);
                A.Tick(Now, Viewers, S, SourceBytes);
            }
        }
    }

    public static void Register()
    {
        T.Add("adapt: two slow links among ten share one re-encode sized for the slower", () =>
        {
            var rig = new Rig();
            for (int i = 0; i < 8; i++) rig.Add($"fast{i}", 20_000);
            var richard = rig.Add("richard", 900);
            var bobby = rig.Add("bobby", 500);
            rig.Run(15);

            T.Eq(1, rig.Started.Count);
            var tier = rig.Started[0];
            // slowest capacity 500 kbit/s × 70% headroom − 64k audio ≈ 286 → 300 (rounded to 50).
            T.Eq(300, tier.Kbps);
            T.True(ReferenceEquals(tier, richard.Rendition));
            T.True(ReferenceEquals(tier, bobby.Rendition));
            foreach (var v in rig.Viewers.Take(8)) T.True(v.Rendition.IsSource);
        });

        T.Add("adapt: when the slowest leaves, the re-encode is retuned for who's left", () =>
        {
            var rig = new Rig();
            rig.Add("fast", 20_000);
            var richard = rig.Add("richard", 900);
            var bobby = rig.Add("bobby", 500);
            rig.Run(15);
            T.Eq(300, rig.Started[0].Kbps);

            rig.Viewers.Remove(bobby);
            rig.Run(22);   // retunes once the encoder is old enough to replace (and before richard would be retried on the source)
            T.Eq(2, rig.Started.Count);
            var retuned = rig.Started[1];
            T.True(retuned.Kbps > 450);                       // sized from richard's ~900 kbit/s now
            T.True(ReferenceEquals(retuned, richard.Rendition));
            T.True(rig.Stopped.Contains(rig.Started[0]));     // the old encoder stopped once nobody was on it
        });

        T.Add("adapt: a recovered viewer goes back to the source; a failed try backs off", () =>
        {
            var rig = new Rig();
            rig.Add("fast", 20_000);
            var richard = rig.Add("richard", 900);
            rig.Run(15);
            T.False(richard.Rendition.IsSource);

            richard.LinkKbps = 20_000;                         // their roommate stopped torrenting
            // Climbs in steps (550 -> 850 -> 1300 -> 2000 kbit/s, one per clean backoff period), then the source.
            var seen = new List<int>();
            for (int i = 0; i < 200 && !richard.Rendition.IsSource; i++)
            {
                rig.Run(1);
                if (!richard.Rendition.IsSource && (seen.Count == 0 || seen[^1] != richard.Rendition.Kbps)) seen.Add(richard.Rendition.Kbps);
            }
            T.True(richard.Rendition.IsSource);
            T.True(seen.Count >= 3);                           // it stepped, rather than jumping straight to the source
            for (int i = 1; i < seen.Count; i++) T.True(seen[i] > seen[i - 1]);
            rig.Run(15);                                       // nobody on the re-encode: it stops
            T.True(rig.Stopped.Contains(rig.Started[0]));

            // And one that still can't take the source: tried, fails, waits twice as long before the next try.
            var rig2 = new Rig();
            var slow = rig2.Add("slow", 900);
            rig2.Run(15 + (int)(Adaptive.InitialBackoffMs / 1000) + 5);   // tier, then a probe of the source...
            rig2.Run(15);                                                  // ...which fails
            T.False(slow.Rendition.IsSource);
            rig2.Run((int)(Adaptive.InitialBackoffMs / 1000) + 5);         // first backoff passed: not yet retried
            T.False(slow.Rendition.IsSource);
        });

        T.Add("adapt: never more re-encodes than allowed; disabling stops them all", () =>
        {
            var rig = new Rig { S = new AdaptSettings { Enabled = true, MaxTranscoders = 2 } };
            rig.Add("a", 3000);    // 2500 + some: fine on the source
            rig.Add("b", 1800);
            rig.Add("c", 700);
            rig.Add("d", 300);
            rig.Run(15);
            T.True(rig.Started.Count is >= 1 and <= 2);
            T.Eq(2, rig.Started.Select(r => r.Kbps).Distinct().Count());   // b and c,d need different bitrates

            rig.S = rig.S with { Enabled = false };
            rig.Run(1);
            T.Eq(rig.Started.Count, rig.Stopped.Count);
            foreach (var v in rig.Viewers) T.True(v.Rendition.IsSource);
        });

        T.Add("adapt: no re-encode that would break the upload budget", () =>
        {
            // Source 2500 kbit/s; a 300k re-encode plus 64k audio needs 2864 kbit/s of upload in total.
            var tight = new Rig { S = new AdaptSettings { Enabled = true, BudgetKbps = 2800 } };
            var a = tight.Add("slow", 500);
            tight.Run(15);
            T.Eq(0, tight.Started.Count);
            T.True(a.Rendition.IsSource);

            var roomy = new Rig { S = new AdaptSettings { Enabled = true, BudgetKbps = 3000 } };
            roomy.Add("slow", 500);
            roomy.Run(15);
            T.Eq(1, roomy.Started.Count);
        });

        T.Add("adapt: VAAPI arguments encode on the GPU", () =>
        {
            var args = Transcoder.Args(450, 360, new AdaptSettings { Encoder = AdaptEncoder.Vaapi, VaapiDevice = "/dev/dri/renderD129" }).ToArray();
            T.True(args.Contains("h264_vaapi"));
            T.True(args.Contains("/dev/dri/renderD129"));
            T.False(args.Contains("libx264"));
            T.True(Array.IndexOf(args, "-vaapi_device") < Array.IndexOf(args, "-i"));   // a global option: before the input
        });

        T.Add("adapt: transcoder arguments keep timestamps and copy the source's keyframes", () =>
        {
            var args = Transcoder.Args(450, 360, new AdaptSettings()).ToArray();
            T.True(args.Contains("-copyts"));
            T.Eq("source", args[Array.IndexOf(args, "-force_key_frames") + 1]);   // keyframes aligned with the source
            T.True(args.Contains("450k"));
            T.Eq("pipe:1", args[^1]);
            T.Eq(240, Transcoder.HeightFor(300));
            T.Eq(720, Transcoder.HeightFor(2000));
        });

        T.Add("flv: frames round-trip through tags", () =>
        {
            var cfg = MediaFrame.CreateConfig(FrameKind.VideoConfig, "avc1.42c01f", 0, 0, [1, 0x42, 0xC0, 0x1F, 0xFF, 0xE1, 0], 1000);
            var key = MediaFrame.Create(FrameKind.Video, true, 1033, [0, 0, 0, 1, 0x65], cfg);
            var tag = Flv.FromFrame(key, 1000)!;
            T.Eq(Flv.TagVideo, tag[0]);
            T.Eq(33, (tag[4] << 16) | (tag[5] << 8) | tag[6]);
            var frames = new List<MediaFrame>();
            var ing = new IngestSession(frames.Add, new NullLog(), fixedStamp: src => 1000 + src);
            ing.OnVideo(0, Flv.FromFrame(cfg, 1000)![11..^4]);
            ing.OnVideo(33, tag[11..^4]);
            T.Eq(2, frames.Count);
            T.Eq(1033.0, frames[1].Ts);
            T.True(frames[1].IsKey);
            T.True(frames[1].Payload.SequenceEqual(key.Payload));
        });
    }
}
