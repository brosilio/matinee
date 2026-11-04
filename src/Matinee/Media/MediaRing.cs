namespace Matinee.Media;

/// <summary>
/// The broadcast buffer: an append-only ring of frames, shared by every viewer. There is one writer (ingest),
/// and each viewer reads at its own cursor (a sequence number). Nothing is copied per viewer.
///
/// Retention is by duration (<see cref="RetainMs"/>), bounded by a hard frame count. In live mode a few seconds
/// is enough to start new viewers on a keyframe; sync mode keeps a minute or more so slow viewers can catch up.
/// </summary>
public sealed class MediaRing
{
    private readonly MediaFrame?[] _buf;
    private readonly long _mask;
    private readonly long[] _keys;          // seqs of video keyframes (a ring of its own)
    private readonly long _keyMask;
    private long _keyHead;                  // number of keyframes ever appended
    private long _head;                     // next seq to write
    private long _tail;                     // oldest valid seq
    private readonly Lock _lock = new();
    private TaskCompletionSource _appended = NewTcs();

    public double RetainMs { get; set; }

    public MediaRing(int capacityPow2 = 1 << 14, double retainMs = 10_000)
    {
        if ((capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("capacity must be a power of two");
        _buf = new MediaFrame?[capacityPow2];
        _mask = capacityPow2 - 1;
        _keys = new long[1024];
        _keyMask = _keys.Length - 1;
        RetainMs = retainMs;
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Head { get { lock (_lock) return _head; } }
    public long Tail { get { lock (_lock) return _tail; } }

    /// <summary>Completes on the next append (or <see cref="Wake"/>). Get it before checking for frames.</summary>
    public Task NextAppend { get { lock (_lock) return _appended.Task; } }

    public void Append(MediaFrame f)
    {
        TaskCompletionSource fire;
        lock (_lock)
        {
            f.Seq = _head;
            _buf[_head & _mask] = f;
            _head++;
            if (f.IsVideo && f.IsKey) _keys[_keyHead++ & _keyMask] = f.Seq;

            // Trim by count, then by age, but never past the newest keyframe (a new viewer needs one to start).
            long latestKey = LatestKeyLocked();
            while (_head - _tail > _buf.Length) Evict();
            while (_tail < _head - 1)
            {
                var old = _buf[_tail & _mask];
                if (old is null) { Evict(); continue; }
                if (f.Ts - old.Ts <= RetainMs) break;
                if (latestKey >= 0 && _tail >= latestKey) break;
                Evict();
            }
            fire = _appended;
            _appended = NewTcs();
        }
        fire.TrySetResult();
    }

    private void Evict()
    {
        _buf[_tail & _mask] = null;
        _tail++;
    }

    /// <summary>Wake every waiter without appending (e.g. the stream ended).</summary>
    public void Wake()
    {
        TaskCompletionSource fire;
        lock (_lock) { fire = _appended; _appended = NewTcs(); }
        fire.TrySetResult();
    }

    /// <summary>The frame at <paramref name="seq"/>, or null if it was evicted or doesn't exist yet.</summary>
    public MediaFrame? Get(long seq)
    {
        lock (_lock)
        {
            if (seq < _tail || seq >= _head) return null;
            return _buf[seq & _mask];
        }
    }

    /// <summary>Seq of the newest video keyframe still buffered, or -1.</summary>
    public long LatestKey() { lock (_lock) return LatestKeyLocked(); }

    private long LatestKeyLocked()
    {
        if (_keyHead == 0) return -1;
        long k = _keys[(_keyHead - 1) & _keyMask];
        return k >= _tail ? k : -1;
    }

    /// <summary>Seq of the first video keyframe at or after <paramref name="seq"/>, or -1.</summary>
    public long KeyAtOrAfter(long seq)
    {
        lock (_lock)
        {
            long n = Math.Min(_keyHead, _keys.Length);
            long found = -1;
            for (long i = 1; i <= n; i++)
            {
                long k = _keys[(_keyHead - i) & _keyMask];
                if (k < _tail || k < seq) break;
                found = k;
            }
            return found;
        }
    }

    /// <summary>Seq of the newest video keyframe at or before media time <paramref name="ts"/>, or -1.</summary>
    public long KeyAtOrBefore(double ts)
    {
        lock (_lock)
        {
            long n = Math.Min(_keyHead, _keys.Length);
            for (long i = 1; i <= n; i++)
            {
                long k = _keys[(_keyHead - i) & _keyMask];
                if (k < _tail) break;
                var f = _buf[k & _mask];
                if (f is not null && f.Ts <= ts) return k;
            }
            return -1;
        }
    }

    /// <summary>Timestamp of the newest frame, or NaN when empty.</summary>
    public double HeadTs
    {
        get
        {
            lock (_lock)
            {
                if (_head == _tail) return double.NaN;
                return _buf[(_head - 1) & _mask]?.Ts ?? double.NaN;
            }
        }
    }

    /// <summary>Timestamp of the oldest buffered frame, or NaN when empty.</summary>
    public double TailTs
    {
        get
        {
            lock (_lock)
            {
                if (_head == _tail) return double.NaN;
                return _buf[_tail & _mask]?.Ts ?? double.NaN;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            while (_tail < _head) Evict();
            _keyHead = 0;
        }
        Wake();
    }
}
