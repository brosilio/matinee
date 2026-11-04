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
    }

    private async ValueTask<HandlerResult> HandleWebSocket(HttpContext ctx)
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

        // Viewers survive reloads: the engine outlives this configuration, so ConfigurationRetired is ignored
        // (reload_drain_timeout still applies; clients reconnect seamlessly). Shutdown and resets come via Aborted.
        await engine.Room.RunViewerAsync(stream, ctx.Connection.RemoteAddress, ctx.Aborted).ConfigureAwait(false);
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

    private static IPEndPoint ParseEndPoint(DirectiveInvocation i)
    {
        var a = i.Args[0];
        if (int.TryParse(a, out int port) && port is > 0 and < 65536) return new IPEndPoint(IPAddress.IPv6Any, port);
        if (IPEndPoint.TryParse(a, out var ep) && a.Contains(':')) return ep;   // port 0: ephemeral (tests)
        throw i.Error($"invalid address \"{a}\", expected [addr:]port");
    }
}
