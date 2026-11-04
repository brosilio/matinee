namespace Matinee.Media;

/// <summary>Settings for automatic low-bitrate renditions (the <c>adapt_*</c> directives).</summary>
public enum AdaptEncoder : byte { X264, Vaapi }

public sealed record AdaptSettings
{
    public bool Enabled { get; init; }
    public string Ffmpeg { get; init; } = "ffmpeg";
    /// <summary>At most this many re-encodes at once (each costs a CPU core or so at 720p).</summary>
    public int MaxTranscoders { get; init; } = 1;
    /// <summary>A viewer losing at least this share of video frames over <see cref="WindowMs"/> is moved down.</summary>
    public double DropThreshold { get; init; } = 0.05;
    /// <summary>How long a viewer is measured before deciding.</summary>
    public int WindowMs { get; init; } = 10_000;
    /// <summary>Share of a viewer's measured throughput the re-encode may use (the rest absorbs jitter).</summary>
    public double Headroom { get; init; } = 0.7;
    public int MinKbps { get; init; } = 200;
    public int AudioKbps { get; init; } = 64;
    public string Preset { get; init; } = "veryfast";
    /// <summary>Video encoder for re-encodes: x264 on the CPU, or the GPU through VAAPI (AMD, Intel).</summary>
    public AdaptEncoder Encoder { get; init; } = AdaptEncoder.X264;
    public string VaapiDevice { get; init; } = "/dev/dri/renderD128";
    /// <summary>
    /// Upload budget (kbit/s) for everything an uplinked home instance sends: the source plus re-encodes. Re-encodes
    /// that wouldn't fit aren't started. 0: no limit.
    /// </summary>
    public int BudgetKbps { get; init; }
    public string[] ExtraArgs { get; init; } = [];

    public bool Equals(AdaptSettings? o) => o is not null && Enabled == o.Enabled && Ffmpeg == o.Ffmpeg &&
        MaxTranscoders == o.MaxTranscoders && DropThreshold == o.DropThreshold && WindowMs == o.WindowMs &&
        Headroom == o.Headroom && MinKbps == o.MinKbps && AudioKbps == o.AudioKbps && Preset == o.Preset &&
        Encoder == o.Encoder && VaapiDevice == o.VaapiDevice && BudgetKbps == o.BudgetKbps &&
        ExtraArgs.AsSpan().SequenceEqual(o.ExtraArgs);

    public override int GetHashCode() => HashCode.Combine(Enabled, Ffmpeg, MaxTranscoders, DropThreshold, MinKbps);
}
