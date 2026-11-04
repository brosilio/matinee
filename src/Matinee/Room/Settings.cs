using System.Net;

namespace Matinee.Room;

public enum StreamMode : byte
{
    /// <summary>Miss packets, miss content: everyone watches the live edge, late data is dropped.</summary>
    Live,
    /// <summary>The room shares one buffered playhead; it pauses for a viewer who falls behind.</summary>
    Sync,
}

/// <summary>The engine's settings, from the <c>matinee { }</c> block of the configuration.</summary>
public sealed record StreamSettings
{
    /// <summary>Viewer password. Null: anyone may watch.</summary>
    public string? Password { get; init; }
    public StreamMode Mode { get; init; } = StreamMode.Live;
    /// <summary>Target playout delay behind the live edge.</summary>
    public int DelayMs { get; init; } = 1000;
    /// <summary>Never let a viewer play further behind live than this (live mode: skip ahead; sync: room catches up).</summary>
    public int MaxDelayMs { get; init; } = 5000;
    /// <summary>Unacknowledged media in flight to a viewer before we start dropping video for them.</summary>
    public int CongestionMs { get; init; } = 1500;
    /// <summary>How much media to keep (sync mode needs a minute or more).</summary>
    public int BufferMs { get; init; } = 10_000;
    public int MaxViewers { get; init; } = 50;

    public IPEndPoint? IngestListen { get; init; }
    /// <summary>Stream key the publisher must use. Null: refuse all publishers.</summary>
    public string? IngestKey { get; init; }

    /// <summary>Optional ffmpeg output URL. When set, incoming FLV tags are re-encoded and published here.</summary>
    public string? ProxyPass { get; init; }
    public string ProxyFfmpeg { get; init; } = "ffmpeg";
    public string[] ProxyArgs { get; init; } =
    [
        "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-bf", "0",
        "-force_key_frames", "expr:gte(t,n_forced*1)",
        "-c:a", "aac", "-b:a", "128k", "-ar", "48000",
    ];

    /// <summary>Home side of an uplink: the key a cloud instance must present to <c>matinee_uplink</c>.</summary>
    public string? UplinkKey { get; init; }
    /// <summary>Cloud side: pull the stream (and re-encodes) from a home instance here, instead of RTMP ingest.</summary>
    public Uri? UplinkPull { get; init; }
    public string? UplinkPullKey { get; init; }

    /// <summary>Automatic low-bitrate re-encodes for viewers on slow links (see <see cref="Adaptive"/>).</summary>
    public Media.AdaptSettings Adapt { get; init; } = new();

    public int EffectiveBufferMs => Mode == StreamMode.Sync ? Math.Max(BufferMs, 60_000) : Math.Max(BufferMs, MaxDelayMs * 2);
}
