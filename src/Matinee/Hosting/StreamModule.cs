using System.Net;
using Httpgrl;
using Matinee.Net;
using Matinee.Room;

namespace Matinee.Hosting;

/// <summary>
/// The httpgrl module. Configuration:
/// <code>
/// matinee {                 # main context
///     ingest_listen 1935;        # RTMP ingest ([addr:]port)
///     ingest_key  "secret";      # stream key the publisher must use
///     proxy_pass  rtmp://cloud/live/secret; # optional: re-encode and republish with ffmpeg
///     proxy_ffmpeg /usr/bin/ffmpeg;
///     proxy_args -c:v libx264 -preset veryfast -tune zerolatency -bf 0 -c:a aac -b:a 128k;
///     password    "hunter2";     # viewers' shared password (omit: open room)
///     mode        live;          # live | sync
///     delay       1s;            # target delay behind live
///     max_delay   5s;            # ceiling
///     congestion  1500ms;        # unacked media in flight before dropping video for a viewer
///     buffer      10s;           # media kept (sync mode: at least 60s)
///     max_viewers 50;
///
///     adapt              on;         # re-encode for viewers who can't keep up (needs ffmpeg)
///     adapt_ffmpeg       ffmpeg;
///     adapt_transcoders  1;          # at most this many re-encodes at once
///     adapt_drop         5%;         # share of video dropped (over adapt_window) that moves a viewer down
///     adapt_window       10s;
///     adapt_headroom     70%;        # share of a viewer's measured throughput a re-encode may use
///     adapt_min_bitrate  200k;       # kbit/s
///     adapt_audio_bitrate 64k;
///     adapt_preset       veryfast;   # x264 preset
///     adapt_args         ...;        # extra ffmpeg output arguments
/// }
/// http { server { location = /ws { matinee_ws; } } }
/// </code>
/// A module instance belongs to one configuration build; the <see cref="Engine"/> outlives them all. The settings
/// parsed here are applied to the engine by the host only once the whole build has succeeded.
/// </summary>
public sealed class StreamModule(Engine engine) : IModule
{
    private static readonly ContextKind Block = new("matinee");
    private const int WsIdleMs = 30_000;

    private StreamSettings _s = new();
    private bool _seenBlock;
    private bool _uplinkEndpoint, _hlsEndpoint;

    public string Name => "matinee";

    /// <summary>The settings this configuration asks for (valid once the build succeeded).</summary>
    public StreamSettings Settings => _s;
    public bool HasBlock => _seenBlock;

    public void Register(IModuleRegistry r)
    {
        ContextKind[] b = [Block];
        r.AddDirective(new() { Name = "matinee", Contexts = [ContextKind.Main], Block = Block, Handler = _ => _seenBlock = true });
        r.AddDirective(new() { Name = "password", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { Password = NonEmpty(i, 0) } });
        r.AddDirective(new()
        {
            Name = "mode", Contexts = b, MinArgs = 1, MaxArgs = 1,
            Handler = i => _s = _s with
            {
                Mode = i.Args[0] switch
                {
                    "live" => StreamMode.Live,
                    "sync" => StreamMode.Sync,
                    _ => throw i.Error("expected \"live\" or \"sync\""),
                },
            },
        });
        r.AddDirective(new() { Name = "delay", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { DelayMs = Range(i, i.Millis(0), 100, 60_000) } });
        r.AddDirective(new() { Name = "max_delay", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { MaxDelayMs = Range(i, i.Millis(0), 500, 120_000) } });
        r.AddDirective(new() { Name = "congestion", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { CongestionMs = Range(i, i.Millis(0), 200, 60_000) } });
        r.AddDirective(new() { Name = "buffer", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { BufferMs = Range(i, i.Millis(0), 1000, 600_000) } });
        r.AddDirective(new() { Name = "max_viewers", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { MaxViewers = Range(i, i.Int(0), 1, 10_000) } });
        r.AddDirective(new() { Name = "ingest_listen", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { IngestListen = ParseEndPoint(i) } });
        r.AddDirective(new() { Name = "ingest_key", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { IngestKey = NonEmpty(i, 0) } });
        r.AddDirective(new() { Name = "proxy_pass", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { ProxyPass = ProxyUrl(i) } });
        r.AddDirective(new() { Name = "proxy_ffmpeg", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { ProxyFfmpeg = NonEmpty(i, 0) } });
        r.AddDirective(new() { Name = "adapt", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { Enabled = i.Flag(0) }) });
        r.AddDirective(new() { Name = "adapt_ffmpeg", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { Ffmpeg = NonEmpty(i, 0) }) });
        r.AddDirective(new() { Name = "adapt_transcoders", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { MaxTranscoders = Range(i, i.Int(0), 1, 16) }) });
        r.AddDirective(new() { Name = "adapt_drop", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { DropThreshold = Percent(i, 0.001, 0.9) }) });
        r.AddDirective(new() { Name = "adapt_window", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { WindowMs = Range(i, i.Millis(0), 2000, 120_000) }) });
        r.AddDirective(new() { Name = "adapt_headroom", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { Headroom = Percent(i, 0.1, 1) }) });
        r.AddDirective(new() { Name = "adapt_min_bitrate", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { MinKbps = Kbps(i, 50, 50_000) }) });
        r.AddDirective(new() { Name = "adapt_audio_bitrate", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { AudioKbps = Kbps(i, 16, 512) }) });
        r.AddDirective(new()
        {
            Name = "adapt_preset", Contexts = b, MinArgs = 1, MaxArgs = 1,
            Handler = i => Adapt(a => a with
            {
                Preset = i.Args[0] is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast" or "medium"
                    ? i.Args[0] : throw i.Error("expected an x264 preset (ultrafast .. medium)"),
            }),
        });
        r.AddDirective(new()
        {
            Name = "adapt_encoder", Contexts = b, MinArgs = 1, MaxArgs = 1,
            Handler = i => Adapt(a => a with
            {
                Encoder = i.Args[0] switch
                {
                    "x264" => Media.AdaptEncoder.X264,
                    "vaapi" => Media.AdaptEncoder.Vaapi,
                    _ => throw i.Error("expected \"x264\" or \"vaapi\""),
                },
            }),
        });
        r.AddDirective(new() { Name = "adapt_vaapi_device", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { VaapiDevice = NonEmpty(i, 0) }) });
        r.AddDirective(new() { Name = "adapt_budget", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Adapt(a => a with { BudgetKbps = Kbps(i, 100, 10_000_000) }) });
        r.AddDirective(new() { Name = "uplink_key", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => _s = _s with { UplinkKey = NonEmpty(i, 0) } });
        r.AddDirective(new()
        {
            Name = "uplink_pull", Contexts = b, MinArgs = 2, MaxArgs = 2,
            Handler = i =>
            {
                if (!Uri.TryCreate(i.Args[0], UriKind.Absolute, out var u) || u.Scheme is not ("ws" or "wss"))
                    throw i.Error($"invalid URL \"{i.Args[0]}\", expected ws:// or wss://");
                _s = _s with { UplinkPull = u, UplinkPullKey = NonEmpty(i, 1) };
            },
        });
        r.AddDirective(new()
        {
            Name = "matinee_uplink", Contexts = [ContextKind.Location],
            Handler = i =>
            {
                var c = i.Conf<LocationHandlerConf>();
                if (c.Handler is not null) throw i.Error($"location already handled by {c.Owner}");
                c.Handler = HandleUplink;
                c.Owner = Name;
                _uplinkEndpoint = true;
            },
        });
        r.AddDirective(new() { Name = "hls", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Hls(h => h with { Enabled = i.Flag(0) }) });
        r.AddDirective(new() { Name = "hls_token", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Hls(h => h with { Token = Token(i) }) });
        r.AddDirective(new() { Name = "hls_window", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Hls(h => h with { Window = Range(i, i.Int(0), 3, 30) }) });
        r.AddDirective(new() { Name = "hls_start", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Hls(h => h with { StartOffsetMs = Range(i, i.Millis(0), 1000, 30_000) }) });
        r.AddDirective(new() { Name = "hls_low", Contexts = b, MinArgs = 1, MaxArgs = 1, Handler = i => Hls(h => h with { LowKbps = i.Args[0] == "off" ? 0 : Kbps(i, 100, 20_000) }) });
        r.AddDirective(new()
        {
            Name = "matinee_hls", Contexts = [ContextKind.Location],
            Handler = i =>
            {
                var c = i.Conf<LocationHandlerConf>();
                if (c.Handler is not null) throw i.Error($"location already handled by {c.Owner}");
                c.Handler = HandleHls;
                c.Owner = Name;
                _hlsEndpoint = true;
            },
        });
        r.AddDirective(new() { Name = "adapt_args", Contexts = b, MinArgs = 1, MaxArgs = 128, Handler = i => Adapt(a => a with { ExtraArgs = [.. i.Args] }) });
        r.AddDirective(new() { Name = "proxy_args", Contexts = b, MinArgs = 1, MaxArgs = 128, Handler = i => _s = _s with { ProxyArgs = [.. i.Args] } });

        r.AddDirective(new()
        {
            Name = "matinee_ws", Contexts = [ContextKind.Location],
            Handler = i =>
            {
                var c = i.Conf<LocationHandlerConf>();
                if (c.Handler is not null) throw i.Error($"location already handled by {c.Owner}");
                c.Handler = HandleWebSocket;
                c.Owner = Name;
            },
        });
    }

    public void Configure(IModuleSetup setup)
    {
        if (_s.DelayMs > _s.MaxDelayMs) throw new ConfigException($"matinee: delay ({_s.DelayMs} ms) exceeds max_delay ({_s.MaxDelayMs} ms)");
        if (_s.IngestListen is not null && _s.IngestKey is null) throw new ConfigException("matinee: ingest_listen needs an ingest_key");
        if (_s.ProxyPass is not null && _s.IngestListen is null) throw new ConfigException("matinee: proxy_pass needs ingest_listen");
        if (_s.Password is null) setup.Log.Warn("matinee: no password set, anyone can watch and chat");
        if (_s.UplinkPull is not null && _s.IngestListen is not null)
            throw new ConfigException("matinee: uplink_pull and ingest_listen are two different sources; use one");
        if (_uplinkEndpoint && _s.UplinkKey is null) throw new ConfigException("matinee: matinee_uplink needs an uplink_key");
        if (_hlsEndpoint && !_s.Hls.Enabled) throw new ConfigException("matinee: matinee_hls needs \"hls on\"");
        if (_s.Hls.Enabled && _s.Hls.Token is null && _s.Password is not null)
            setup.Log.Warn("matinee: hls has no hls_token, so anyone with the URL can watch (the password only guards the web player)");
        if (_s.Hls.Enabled && _s.Hls.StartOffsetMs + 1500 > _s.Hls.Window * 1000)
            throw new ConfigException("matinee: hls_start must leave room in hls_window (a segment is about 1 s)");
        if (_s.Adapt.Enabled && _s.UplinkPull is null && _s.Adapt.BudgetKbps > 0)
            setup.Log.Warn("matinee: adapt_budget only applies to re-encodes made by an uplinked home instance");
    }

    private ValueTask<HandlerResult> HandleWebSocket(HttpContext ctx) =>
        // Viewers survive reloads: the engine outlives this configuration, so ConfigurationRetired is ignored
        // (reload_drain_timeout still applies; clients reconnect seamlessly). Shutdown and resets come via Aborted.
        Upgrade(ctx, stream => engine.Room.RunViewerAsync(stream, ctx.Connection.RemoteAddress, ctx.Aborted));

    private ValueTask<HandlerResult> HandleUplink(HttpContext ctx) =>
        Upgrade(ctx, stream => UplinkServer.ServeAsync(stream, engine, ctx.Log, ctx.Connection.RemoteAddress, ctx.Aborted));

    /// <summary>Validate a WebSocket upgrade, switch protocols, and run <paramref name="serve"/> on the stream.</summary>
    private static async ValueTask<HandlerResult> Upgrade(HttpContext ctx, Func<Stream, Task> serve)
    {
        var req = ctx.Request;
        if (!ReferenceEquals(req.Method, HttpMethods.Get)) return HandlerResult.WithStatus(405);
        if (!HasToken(req.Headers.Get("Upgrade"), "websocket") || !HasToken(req.Headers.Get("Connection"), "upgrade"))
            return HandlerResult.WithStatus(426);
        if (req.Headers.Get("Sec-WebSocket-Version") != "13") return HandlerResult.WithStatus(426);
        var accept = WsHandshake.Accept(req.Headers.Get("Sec-WebSocket-Key"));
        if (accept is null) return HandlerResult.WithStatus(400);

        ctx.Response.Status = 101;
        ctx.Response.Headers.Set("Upgrade", "websocket");
        ctx.Response.Headers.Set("Connection", "Upgrade");
        ctx.Response.Headers.Set("Sec-WebSocket-Accept", accept);
        var stream = await ctx.UpgradeAsync(WsIdleMs).ConfigureAwait(false);
        await serve(stream).ConfigureAwait(false);
        return HandlerResult.Done;
    }

    private static bool HasToken(string? header, string token)
    {
        if (header is null) return false;
        foreach (var part in header.Split(','))
            if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string NonEmpty(DirectiveInvocation i, int n) =>
        i.Args[n].Length > 0 ? i.Args[n] : throw i.Error("must not be empty");

    private static string ProxyUrl(DirectiveInvocation i, int n = 0)
    {
        string value = NonEmpty(i, n);
        return Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme is "rtmp" or "rtmps" && !string.IsNullOrEmpty(u.Host)
            ? value
            : throw i.Error("expected an absolute rtmp:// or rtmps:// URL");
    }

    private static int Range(DirectiveInvocation i, int v, int min, int max) =>
        v >= min && v <= max ? v : throw i.Error($"must be between {min} and {max}");

    private void Hls(Func<Room.HlsSettings, Room.HlsSettings> change) => _s = _s with { Hls = change(_s.Hls) };

    private static string Token(DirectiveInvocation i)
    {
        var t = NonEmpty(i, 0);
        foreach (char ch in t)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) throw i.Error("letters, digits, - and _ only (it goes in a URL)");
        if (t.Length < 16) throw i.Error("at least 16 characters (it's the only thing guarding the stream)");
        return t;
    }

    /// <summary>
    /// HLS: <c>…/[token/]index.m3u8</c> (master), <c>…/[token/]src.m3u8</c> or <c>low.m3u8</c> (variants),
    /// <c>…/[token/]src/123.ts</c> (segments).
    /// </summary>
    private async ValueTask<HandlerResult> HandleHls(HttpContext ctx)
    {
        var hls = engine.Hls;
        if (hls is null) return HandlerResult.WithStatus(404);
        if (!ctx.Request.IsGetOrHead) return HandlerResult.WithStatus(405);
        var parts = ctx.Request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return HandlerResult.WithStatus(404);
        string file = parts[^1];
        bool segment = file.EndsWith(".ts", StringComparison.Ordinal);
        int tokenAt = parts.Length - (segment ? 3 : 2);
        var token = hls.Settings.Token;
        if (token is not null && (tokenAt < 0 || !SameSecret(parts[tokenAt], token))) return HandlerResult.WithStatus(404);
        hls.Touch();

        byte[] body;
        string type;
        if (segment)
        {
            if (parts.Length < 2 || hls.Find(parts[^2]) is not { } v
                || !long.TryParse(file.AsSpan(0, file.Length - 3), System.Globalization.NumberStyles.None, null, out long seq)
                || v.Get(seq) is not { } seg)
                return HandlerResult.WithStatus(404);
            body = seg.Data;
            type = "video/mp2t";
            ctx.Response.Headers.Set("Cache-Control", "max-age=60");
        }
        else if (file == "index.m3u8")
        {
            body = System.Text.Encoding.UTF8.GetBytes(hls.Master());
            type = "application/vnd.apple.mpegurl";
            ctx.Response.Headers.Set("Cache-Control", "no-cache");
        }
        else if (file.EndsWith(".m3u8", StringComparison.Ordinal) && hls.VariantPlaylist(file[..^5]) is { } pl)
        {
            body = System.Text.Encoding.UTF8.GetBytes(pl);
            type = "application/vnd.apple.mpegurl";
            ctx.Response.Headers.Set("Cache-Control", "no-cache");
        }
        else return HandlerResult.WithStatus(404);

        ctx.Response.Headers.Set("Content-Type", type);
        ctx.Response.Headers.Set("Access-Control-Allow-Origin", "*");
        ctx.Response.ContentLength = body.Length;
        if (!ctx.Request.IsHead) await ctx.Response.WriteAsync(body).ConfigureAwait(false);
        return HandlerResult.Done;
    }

    private static bool SameSecret(string a, string b)
    {
        Span<byte> x = stackalloc byte[32], y = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a), x);
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(b), y);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(x, y);
    }

    private void Adapt(Func<Media.AdaptSettings, Media.AdaptSettings> change) => _s = _s with { Adapt = change(_s.Adapt) };

    /// <summary>"5%" or a fraction ("0.05").</summary>
    private static double Percent(DirectiveInvocation i, double min, double max)
    {
        var a = i.Args[0];
        bool pct = a.EndsWith('%');
        if (!double.TryParse(pct ? a[..^1] : a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
            throw i.Error($"invalid value \"{a}\", expected a percentage like 5%");
        if (pct) v /= 100;
        return v >= min && v <= max ? v : throw i.Error($"must be between {min * 100}% and {max * 100}%");
    }

    /// <summary>A bitrate in kbit/s: "200k", "1m", or a bare number of kbit/s.</summary>
    private static int Kbps(DirectiveInvocation i, int min, int max)
    {
        var a = i.Args[0];
        int mul = 1;
        if (a.EndsWith('k') || a.EndsWith('K')) a = a[..^1];
        else if (a.EndsWith('m') || a.EndsWith('M')) { a = a[..^1]; mul = 1000; }
        if (!int.TryParse(a, System.Globalization.NumberStyles.None, null, out var v)) throw i.Error($"invalid bitrate \"{i.Args[0]}\"");
        return Range(i, v * mul, min, max);
    }

    private static IPEndPoint ParseEndPoint(DirectiveInvocation i)
    {
        var a = i.Args[0];
        if (int.TryParse(a, out int port) && port is > 0 and < 65536) return new IPEndPoint(IPAddress.IPv6Any, port);
        if (IPEndPoint.TryParse(a, out var ep) && a.Contains(':')) return ep;   // port 0: ephemeral (tests)
        throw i.Error($"invalid address \"{a}\", expected [addr:]port");
    }
}
