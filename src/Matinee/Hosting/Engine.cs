using System.Security.Cryptography;
using System.Text;
using Httpgrl;
using Matinee.Media;
using Matinee.Room;
using Matinee.Rtmp;

namespace Matinee.Hosting;

/// <summary>
/// Everything that outlives an httpgrl configuration reload: the room (viewers, chat, media buffer) and the
/// RTMP ingest. The httpgrl module is rebuilt on every reload and just points WebSocket requests here.
/// </summary>
public sealed class Engine : IRtmpHandler, IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _lifetime = new(1, 1);
    private RtmpServer? _rtmp;
    private RtmpSession? _publisher;
    private IngestSession? _ingest;
    private FfmpegRelay? _relay;
    private UplinkClient? _uplink;
    private readonly HashSet<Task> _relayCleanups = [];
    private bool _disposed;

    public Room.Room Room { get; }
    public StreamSettings Settings { get; private set; }

    public Engine(StreamSettings settings, ILogger log)
    {
        _log = log;
        Settings = settings;
        Room = new Room.Room(settings, log);
    }

    public System.Net.IPEndPoint? IngestEndPoint => _rtmp?.EndPoint;

    /// <summary>Apply settings: at startup, and after each successful reload.</summary>
    public async Task ApplyAsync(StreamSettings s)
    {
        await _lifetime.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var old = Settings;
            bool proxyChanged = old.ProxyPass != s.ProxyPass || old.ProxyFfmpeg != s.ProxyFfmpeg ||
                !old.ProxyArgs.AsSpan().SequenceEqual(s.ProxyArgs);
            Settings = s;
            Room.Apply(s);
            if (old.UplinkPull != s.UplinkPull || old.UplinkPullKey != s.UplinkPullKey || (_uplink is null && s.UplinkPull is not null))
            {
                if (_uplink is not null) await _uplink.DisposeAsync().ConfigureAwait(false);
                _uplink = null;
                Room.RemoteEncoders = null;
                if (s.UplinkPull is not null)
                {
                    _uplink = new UplinkClient(Room, s.UplinkPull, s.UplinkPullKey ?? "", _log);
                    Room.RemoteEncoders = _uplink.Request;
                    _uplink.Start();
                }
            }
            if (_rtmp is null || !Equals(old.IngestListen, s.IngestListen))
            {
                if (_rtmp is not null) await _rtmp.DisposeAsync().ConfigureAwait(false);
                _rtmp = null;
                if (s.IngestListen is not null)
                {
                    _rtmp = new RtmpServer(s.IngestListen, this, _log);
                    _rtmp.Start();
                }
            }
            else if (proxyChanged)
            {
                RtmpSession? publisher;
                lock (_lock) publisher = _publisher;
                if (publisher is not null)
                {
                    _log.Info("proxy: settings changed; reconnecting the publisher");
                    publisher.Abort?.Invoke();
                }
            }
        }
        finally { _lifetime.Release(); }
    }

    // ---- IRtmpHandler ----

    public bool OnPublish(RtmpSession session, string app, string streamName)
    {
        if (!KeyMatches(streamName)) return false;
        FfmpegRelay? relay = null;
        if (Settings.ProxyPass is not null)
        {
            try { relay = FfmpegRelay.Start(Settings, _log, () => session.Abort?.Invoke()); }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Error("proxy: cannot start ffmpeg", new LogField("error", e.Message));
                return false;
            }
        }
        RtmpSession? previous;
        FfmpegRelay? previousRelay;
        lock (_lock)
        {
            previous = _publisher;
            previousRelay = _relay;
            _publisher = session;
            _ingest = new IngestSession(Room.Append, _log);
            _relay = relay;
        }
        if (previousRelay is not null) QueueRelayCleanup(previousRelay, graceful: false);
        if (previous is not null)
        {
            _log.Info("ingest: new publisher replaces the old one", new("old", previous.Remote), new("new", session.Remote));
            previous.Abort?.Invoke();
        }
        Room.StreamStarted();
        return true;
    }

    public void OnVideo(RtmpSession session, uint timestamp, ReadOnlySpan<byte> body)
    {
        var (ingest, relay) = Current(session);
        ingest?.OnVideo(timestamp, body);
        relay?.WriteVideo(timestamp, body);
    }

    public void OnAudio(RtmpSession session, uint timestamp, ReadOnlySpan<byte> body)
    {
        var (ingest, relay) = Current(session);
        ingest?.OnAudio(timestamp, body);
        relay?.WriteAudio(timestamp, body);
    }

    public void OnUnpublish(RtmpSession session)
    {
        FfmpegRelay? relay;
        lock (_lock)
        {
            if (_publisher != session) return;   // replaced: the new one is live
            _publisher = null;
            _ingest = null;
            relay = _relay;
            _relay = null;
        }
        if (relay is not null) QueueRelayCleanup(relay, graceful: true);
        _log.Info("ingest: publisher left", new LogField("remote", session.Remote));
        Room.StreamEnded();
    }

    private (IngestSession? Ingest, FfmpegRelay? Relay) Current(RtmpSession session)
    {
        lock (_lock) return _publisher == session ? (_ingest, _relay) : (null, null);
    }

    private void QueueRelayCleanup(FfmpegRelay relay, bool graceful)
    {
        Task task;
        lock (_lock)
        {
            task = relay.StopAsync(graceful);
            _relayCleanups.Add(task);
            _ = task.ContinueWith(static (t, state) =>
            {
                var engine = (Engine)state!;
                lock (engine._lock) engine._relayCleanups.Remove(t);
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private bool KeyMatches(string given)
    {
        var want = Settings.IngestKey;
        if (want is null) return false;
        Span<byte> a = stackalloc byte[32], b = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(want), a);
        SHA256.HashData(Encoding.UTF8.GetBytes(given), b);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            var rtmp = _rtmp;
            _rtmp = null;
            if (rtmp is not null) await rtmp.DisposeAsync().ConfigureAwait(false);
            if (_uplink is not null) await _uplink.DisposeAsync().ConfigureAwait(false);
            _uplink = null;
            Task[] relayCleanups;
            lock (_lock) relayCleanups = [.. _relayCleanups];
            if (relayCleanups.Length > 0) await Task.WhenAll(relayCleanups).ConfigureAwait(false);
            await Room.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifetime.Release(); }
    }
}
