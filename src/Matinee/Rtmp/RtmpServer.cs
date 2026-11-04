using System.Net;
using System.Net.Sockets;
using Httpgrl;

namespace Matinee.Rtmp;

/// <summary>RTMP ingest listener. Few connections are expected (one publisher), so each gets a plain task.</summary>
public sealed class RtmpServer(IPEndPoint endpoint, IRtmpHandler handler, ILogger log) : IAsyncDisposable
{
    private const int MaxConnections = 8;
    private const int IdleTimeoutMs = 30_000;

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _sessionsLock = new();
    private readonly HashSet<Task> _sessions = [];
    private Socket? _listener;
    private Task? _accept;
    private int _active;
    private int _disposed;

    public IPEndPoint EndPoint => (IPEndPoint?)_listener?.LocalEndPoint ?? endpoint;

    public void Start()
    {
        var s = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6) s.DualMode = endpoint.Address.Equals(IPAddress.IPv6Any);
            s.Bind(endpoint);
            s.Listen(16);
        }
        catch
        {
            s.Dispose();
            throw;
        }
        _listener = s;
        _accept = AcceptLoop();
        log.Info("rtmp: listening", new LogField("endpoint", EndPoint.ToString()));
    }

    private async Task AcceptLoop()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket c;
            try { c = await _listener!.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException e)
            {
                log.Warn("rtmp: accept failed", new LogField("error", e.Message));
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            if (Interlocked.Increment(ref _active) > MaxConnections)
            {
                Interlocked.Decrement(ref _active);
                c.Dispose();
                continue;
            }
            // Keep every session rooted and awaitable. In particular, shutdown must not leave a publisher's
            // NetworkStream read running after the engine and logger have been torn down.
            lock (_sessionsLock)
            {
                var task = Serve(c, ct);
                _sessions.Add(task);
                _ = task.ContinueWith(static (t, state) =>
                {
                    var server = (RtmpServer)state!;
                    lock (server._sessionsLock) server._sessions.Remove(t);
                }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task Serve(Socket c, CancellationToken stop)
    {
        string remote = c.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            c.NoDelay = true;
            using var ns = new NetworkStream(c, ownsSocket: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            // Cancellation of Socket/NetworkStream reads is platform-dependent. Closing the exact accepted socket
            // guarantees that server shutdown and publisher replacement wake a blocked read immediately.
            using var close = cts.Token.UnsafeRegister(static state => ((Socket)state!).Dispose(), c);
            var session = new RtmpSession(ns, handler, log, remote);
            session.Abort = () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };
            var run = session.RunAsync(cts.Token);
            long seen = -1;
            long lastProgress = Environment.TickCount64;
            while (!run.IsCompleted)
            {
                await Task.WhenAny(run, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
                long r = session.Received;
                if (r != seen) { seen = r; lastProgress = Environment.TickCount64; }
                else if (Environment.TickCount64 - lastProgress > IdleTimeoutMs)
                {
                    log.Warn("rtmp: idle timeout", new LogField("remote", remote));
                    cts.Cancel();
                    c.Dispose();
                    break;
                }
            }
            try { await run.ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or IOException or EndOfStreamException or SocketException or ObjectDisposedException) { }
            catch (Exception e) when (e is InvalidDataException or FormatException)
            {
                log.Warn("rtmp: protocol error", new("remote", remote), new("error", e.Message));
            }
        }
        catch (Exception e)
        {
            log.Error("rtmp: session failed", new("remote", remote), new("error", e.ToString()));
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener?.Dispose();
        if (_accept is not null) await _accept.ConfigureAwait(false);

        Task[] sessions;
        lock (_sessionsLock) sessions = [.. _sessions];
        if (sessions.Length > 0) await Task.WhenAll(sessions).ConfigureAwait(false);
        _stop.Dispose();
    }
}
