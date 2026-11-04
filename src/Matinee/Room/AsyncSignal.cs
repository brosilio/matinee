namespace Matinee.Room;

/// <summary>An auto-reset event for one awaiting consumer. Set is cheap and idempotent until consumed.</summary>
internal sealed class AsyncSignal
{
    private readonly SemaphoreSlim _sem = new(0, 1);
    private int _set;

    public void Set()
    {
        if (Interlocked.Exchange(ref _set, 1) == 0) _sem.Release();
    }

    /// <summary>Wait for a Set. Re-arms before returning, so check your queues after this, not before.</summary>
    public async Task<bool> WaitAsync(int timeoutMs, CancellationToken ct)
    {
        bool got = await _sem.WaitAsync(timeoutMs, ct).ConfigureAwait(false);
        if (got) Volatile.Write(ref _set, 0);
        return got;
    }
}
