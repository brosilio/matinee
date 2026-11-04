namespace Matinee.Media;

/// <summary>Something producing a rendition: a local ffmpeg (<see cref="Transcoder"/>) or a remote one.</summary>
internal interface IEncoder
{
    bool Failed { get; }
    Task StopAsync();
}

/// <summary>
/// One version of the stream viewers can watch: the source as ingested, or a lower-bitrate re-encode of it. Every
/// rendition's frames are stamped on the same server clock, so a viewer can switch between them at any keyframe
/// without a jump in time.
/// </summary>
public sealed class Rendition
{
    private static int s_nextId;

    public int Id { get; } = Interlocked.Increment(ref s_nextId);
    public MediaRing Ring { get; }
    /// <summary>Target video bitrate (kbit/s); 0 for the source.</summary>
    public int Kbps { get; }
    public int Height { get; }
    public string Label { get; }
    public double CreatedAt { get; } = Clock.NowMs;
    /// <summary>What produces a re-encode: a local ffmpeg, or one on an uplinked instance.</summary>
    internal IEncoder? Encoder { get; init; }

    public bool IsSource { get; }
    /// <summary>Has a keyframe to start viewers on, and (for a re-encode) its encoder is still running.</summary>
    public bool Ready => Ring.LatestKey() >= 0 && !Failed;
    public bool Failed => Encoder is { Failed: true };

    internal Rendition(MediaRing ring, int kbps, int height, string label, bool isSource = false)
    {
        IsSource = isSource;
        Ring = ring;
        Kbps = kbps;
        Height = height;
        Label = label;
    }

    public static Rendition Source(MediaRing ring) => new(ring, 0, 0, "source", isSource: true);

    public override string ToString() => Label;
}
