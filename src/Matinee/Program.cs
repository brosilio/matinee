using System.Net.Sockets;
using System.Runtime.InteropServices;
using Httpgrl;
using Httpgrl.Core.Hosting;
using Httpgrl.Core.Logging;
using Matinee.Hosting;
using Matinee.Room;

string config = "conf/matinee.conf";
bool testOnly = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-c" when i + 1 < args.Length: config = args[++i]; break;
        case "-t": testOnly = true; break;
        case "-v":
            Console.Out.WriteLine("matinee " + typeof(Engine).Assembly.GetName().Version);
            return 0;
        case "-h" or "--help":
            Console.Out.WriteLine("usage: matinee [-c config] [-t] [-v]\n  -c  configuration file (default conf/matinee.conf)\n  -t  test configuration and exit\n  -v  print version and exit");
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument \"{args[i]}\" (try -h)");
            return 2;
    }
}

using var log = JsonLogger.Stdout();
await using var engine = new Engine(new StreamSettings(), log);

// The module list is built again on every reload; remember the newest matinee module so its settings can be
// applied to the engine once that build has fully succeeded.
StreamModule? built = null;
IModule[] Modules()
{
    var m = new StreamModule(engine);
    built = m;
    return
    [
        new Httpgrl.Modules.Rewrite.RewriteModule(),
        new Httpgrl.Modules.Rewrite.MapModule(),
        new Httpgrl.Modules.Headers.HeadersModule(),
        new Httpgrl.Modules.Limits.LimitReqModule(),
        new Httpgrl.Modules.Limits.LimitConnModule(),
        new Httpgrl.Modules.Access.AccessModule(),
        new Httpgrl.Modules.Status.StatusModule(),
        new Httpgrl.Modules.Compression.CompressionModule(),
        new Httpgrl.Modules.Proxy.ProxyModule(),
        m,
        .. Httpgrl.Modules.StaticFiles.StaticFiles.Modules(),
    ];
}

HttpServer server;
try
{
    server = ServerBuilder.Load(config, Modules, log);
}
catch (ConfigException e)
{
    log.Error("configuration error", new("error", e.Message), new("at", e.Where?.ToString()));
    return 1;
}
catch (InvalidOperationException e)
{
    log.Error("module error", new LogField("error", e.Message));
    return 1;
}

if (testOnly)
{
    log.Info("configuration ok", new LogField("config", Path.GetFullPath(config)));
    return 0;
}

try
{
    await engine.ApplyAsync(built!.Settings);
    server.Start();
}
catch (Exception e) when (e is IOException or SocketException)
{
    log.Error("startup failed", new LogField("error", e.Message));
    return 1;
}
log.Info("started", new("pid", (long)Environment.ProcessId), new("ingest", engine.IngestEndPoint?.ToString()));

var stop = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
void OnSignal(PosixSignalContext c)
{
    c.Cancel = true;
    stop.TrySetResult(c.Signal.ToString());
}
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
using var sigQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal);
using var sigHup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, c =>
{
    c.Cancel = true;
    log.Info("reloading", new LogField("config", Path.GetFullPath(config)));
    _ = Task.Run(async () =>
    {
        if (!await server.ReloadAsync().ConfigureAwait(false)) return;
        try { await engine.ApplyAsync(built!.Settings).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or SocketException)
        {
            log.Error("reload: applying stream settings failed", new LogField("error", e.Message));
        }
    });
});

var signal = await stop.Task;
log.Info("shutting down", new("signal", signal), new("active", (long)server.ActiveConnections));
await engine.DisposeAsync();
await server.StopAsync(server.ShutdownTimeoutMs);
log.Info("stopped");
return 0;
