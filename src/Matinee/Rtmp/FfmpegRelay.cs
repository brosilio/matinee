using System.Buffers.Binary;
using System.Diagnostics;
using Httpgrl;
using Matinee.Room;

namespace Matinee.Rtmp;

/// <summary>
/// Feeds the publisher's FLV tags to ffmpeg over stdin. ffmpeg re-encodes and republishes to a remote RTMP server.
/// The queue is byte-bounded: a relay that cannot keep up is cut and re-established by the publisher rather than
/// consuming the machine's memory or feeding ffmpeg a broken predictive stream.
/// </summary>
internal sealed class FfmpegRelay
{
    private const int MaxQueuedBytes = 16 << 20;

    private readonly Process _process;
    private readonly ILogger _log;
    private readonly string _secretUrl;
    private readonly Action _failed;
    private readonly Lock _lock = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly CancellationTokenSource _abort = new();
    private readonly Task _pump;
    private readonly Task _stderr;
    private int _queuedBytes;
    private bool _completed;
    private int _failureSignaled;
    private Task? _stopTask;

    private FfmpegRelay(Process process, ILogger log, string secretUrl, Action failed)
    {
        _process = process;
        _log = log;
        _secretUrl = secretUrl;
        _failed = failed;
        _pump = PumpAsync();
        _stderr = ReadStderrAsync();
    }

    public static FfmpegRelay Start(StreamSettings settings, ILogger log, Action failed)
    {
        string target = settings.ProxyPass!;
        var psi = new ProcessStartInfo
        {
            FileName = settings.ProxyFfmpeg,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string[] before = ["-hide_banner", "-loglevel", "warning", "-nostdin", "-f", "flv", "-i", "pipe:0"];
        foreach (string arg in before) psi.ArgumentList.Add(arg);
        foreach (string arg in settings.ProxyArgs) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("flv");
        psi.ArgumentList.Add(target);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg did not start");
        }
        catch
        {
            process.Dispose();
            throw;
        }

        string endpoint = Uri.TryCreate(target, UriKind.Absolute, out var u)
            ? $"{u.Scheme}://{u.Host}{(u.Port > 0 ? ":" + u.Port : "")}"
            : "remote";
        log.Info("proxy: ffmpeg started", new("pid", (long)process.Id), new("target", endpoint));
        return new FfmpegRelay(process, log, target, failed);
    }

    public void WriteVideo(uint timestamp, ReadOnlySpan<byte> body) => Enqueue(9, timestamp, body);
    public void WriteAudio(uint timestamp, ReadOnlySpan<byte> body) => Enqueue(8, timestamp, body);

    private void Enqueue(byte type, uint timestamp, ReadOnlySpan<byte> body)
    {
        var tag = BuildTag(type, timestamp, body);
        bool wake = false, overflow = false;
        lock (_lock)
        {
            if (_completed) return;
            if (_queuedBytes + tag.Length > MaxQueuedBytes) overflow = true;
            else
            {
                wake = _queue.Count == 0;
                _queue.Enqueue(tag);
                _queuedBytes += tag.Length;
            }
        }
        if (overflow)
        {
            _log.Error("proxy: ffmpeg input fell behind", new LogField("queued_bytes", (long)Volatile.Read(ref _queuedBytes)));
            SignalFailure();
        }
        else if (wake)
        {
            try { _ready.Release(); } catch (SemaphoreFullException) { }
        }
    }

    internal static byte[] BuildTag(byte type, uint timestamp, ReadOnlySpan<byte> body) => Media.Flv.Tag(type, timestamp, body);

    private async Task PumpAsync()
    {
        try
        {
            var output = _process.StandardInput.BaseStream;
            await output.WriteAsync(Media.Flv.Header.ToArray(), _abort.Token).ConfigureAwait(false);
            while (true)
            {
                byte[]? tag = null;
                bool completed;
                lock (_lock)
                {
                    if (_queue.TryDequeue(out tag)) _queuedBytes -= tag.Length;
                    completed = _completed;
                }
                if (tag is not null)
                {
                    await output.WriteAsync(tag, _abort.Token).ConfigureAwait(false);
                    continue;
                }
                if (completed) break;
                await _ready.WaitAsync(_abort.Token).ConfigureAwait(false);
            }
            await output.FlushAsync(_abort.Token).ConfigureAwait(false);
            output.Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!_abort.IsCancellationRequested)
            {
                _log.Error("proxy: ffmpeg input failed", new LogField("error", Scrub(e.Message)));
                SignalFailure();
            }
        }
    }

    private async Task ReadStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                _log.Warn("proxy: ffmpeg", new LogField("line", Scrub(line)));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private string Scrub(string text) => text.Replace(_secretUrl, "[relay-url]", StringComparison.Ordinal);

    private void SignalFailure()
    {
        if (Interlocked.Exchange(ref _failureSignaled, 1) == 0) _failed();
    }

    public Task StopAsync(bool graceful)
    {
        lock (_lock) return _stopTask ??= StopCoreAsync(graceful);
    }

    private async Task StopCoreAsync(bool graceful)
    {
        lock (_lock)
        {
            _completed = true;
            if (!graceful) _queue.Clear();
            _queuedBytes = graceful ? _queuedBytes : 0;
        }
        try { _ready.Release(); } catch (SemaphoreFullException) { }
        if (!graceful) _abort.Cancel();

        // A graceful stop drains the queue into ffmpeg, but ffmpeg may have stopped reading (a hung remote), and a
        // pending pipe write doesn't reliably honour cancellation. Bound the drain: kill ffmpeg, which fails the write.
        if (await Task.WhenAny(_pump, Task.Delay(3000)).ConfigureAwait(false) != _pump)
        {
            _log.Warn("proxy: ffmpeg stopped reading; killing it");
            _abort.Cancel();
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        try { await _pump.ConfigureAwait(false); } catch (Exception) { }
        if (graceful)
        {
            using var timeout = new CancellationTokenSource(3000);
            try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        try { await _process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
        try { await _stderr.ConfigureAwait(false); } catch (Exception) { }
        int code = _process.HasExited ? _process.ExitCode : -1;
        _log.Info("proxy: ffmpeg stopped", new("pid", (long)_process.Id), new("exit_code", (long)code));
        _process.Dispose();
        _abort.Dispose();
        _ready.Dispose();
    }
}
