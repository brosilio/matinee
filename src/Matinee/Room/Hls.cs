using System.Globalization;
using System.Text;
using Httpgrl;
using Matinee.Media;

namespace Matinee.Room;

/// <summary>HLS output settings (the <c>hls*</c> directives).</summary>
public sealed record HlsSettings
{
    public bool Enabled { get; init; }
    /// <summary>A secret path segment: VRChat's players can't send passwords, so the URL is the key.</summary>
    public string? Token { get; init; }
    /// <summary>Segments listed in a playlist. Short: a player that stalls falls off the end and has to rejoin near live.</summary>
    public int Window { get; init; } = 6;
    /// <summary>Where players start, behind the newest segment's end (EXT-X-START). Same for everyone: same picture.</summary>
    public int StartOffsetMs { get; init; } = 3000;
    /// <summary>A low-bitrate variant (kbit/s) made while HLS viewers are around; 0: source only.</summary>
    public int LowKbps { get; init; }
}

/// <summary>
/// HLS output for players we don't control, such as VRChat's (PC and Quest): MPEG-TS segments cut from the ring, no
/// re-encoding.
///
/// <b>Everyone together.</b> HLS players can't be told when to play, only what's on offer and where to start, so:
/// <list type="bullet">
/// <item>Segments are cut at the source's keyframes (normally every second), on the server clock, and the low
/// variant is cut at exactly the same timestamps with the same sequence numbers (its keyframes are copied from the
/// source's). Players and variants line up segment for segment, and switching variants is seamless.</item>
/// <item>Playlists ask players to start at the same moment: <see cref="HlsSettings.StartOffsetMs"/> behind now
/// (<c>EXT-X-START</c>, precise, computed per request from how long ago the newest segment ended). So viewers who
/// join at different times, on either variant, land on the same frame.</item>
/// <item>The playlist window is short, so a player that stalls falls off its end and rejoins near live instead of
/// drifting further behind for the rest of the night.</item>
/// <item><c>EXT-X-PROGRAM-DATE-TIME</c> on every segment, for players that can sync to it.</item>
/// </list>
/// HLS viewers end up about <see cref="HlsSettings.StartOffsetMs"/> behind the source (plus their player's own startup);
/// set the room's <c>delay</c> to match and web viewers see the same moment too.
/// </summary>
internal sealed class Hls : IAsyncDisposable
{
    private const double MinSegmentMs = 900;
    private const double LowIdleStopMs = 60_000, LowRetryMs = 10_000;

    private readonly Room _room;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Variant _source;
    private Variant? _low;
    private Rendition? _lowRendition;
    private double _lastRequest = double.NegativeInfinity, _lowRetryAt = double.NegativeInfinity;
    private readonly Lock _lock = new();

    public HlsSettings Settings { get; set; }

    public Hls(Room room, HlsSettings settings, ILogger log)
    {
        _room = room;
        _log = log;
        Settings = settings;
        _source = new Variant("src", room.Ring, boundaries: null, settings);
        _loop = LoopAsync();
    }

    internal sealed record Segment(long Seq, double StartTs, double DurationMs, byte[] Data, DateTimeOffset Wall, bool Discontinuity);

    /// <summary>One rendition's segments.</summary>
    internal sealed class Variant(string name, MediaRing ring, Variant? boundaries, HlsSettings settings)
    {
        public readonly string Name = name;
        public readonly MediaRing Ring = ring;
        private readonly Variant? _leader = boundaries;     // null: we decide the cut points (the source)
        private readonly TsMuxer _mux = new();
        private readonly List<Segment> _segments = new();
        private readonly List<(long Seq, double Ts)> _cuts = new();   // the leader's recent cut points
        private readonly Lock _lock = new();
        private long _cursor = -1, _nextSeq;
        private MemoryStream? _cur;
        private double _curStart;
        private long _curSeq;
        private bool _curDisc, _nextDisc;
        private MediaFrame? _lastVideoCfg;
        private bool _warned;
        public HlsSettings Settings = settings;
        public string? VideoCodec { get; private set; }
        public string? AudioCodec { get; private set; }

        public Segment? Get(long seq)
        {
            lock (_lock) foreach (var s in _segments) if (s.Seq == seq) return s;
            return null;
        }

        public Segment[] Recent()
        {
            lock (_lock)
            {
                int n = Math.Min(Settings.Window, _segments.Count);
                return _segments.GetRange(_segments.Count - n, n).ToArray();
            }
        }

        /// <summary>Peak bitrate of the recent segments (bit/s), for the master playlist.</summary>
        public long Bandwidth()
        {
            long best = 0;
            foreach (var s in Recent()) if (s.DurationMs > 0) best = Math.Max(best, (long)(s.Data.Length * 8 / (s.DurationMs / 1000)));
            return best;
        }

        private long CutAt(double ts)
        {
            lock (_lock)
                for (int i = _cuts.Count - 1; i >= 0; i--)
                    if (Math.Abs(_cuts[i].Ts - ts) < 50) return _cuts[i].Seq;
            return -1;
        }

        /// <summary>Consume what the ring has. <paramref name="log"/> for codec trouble, once.</summary>
        public void Pump(ILogger log)
        {
            for (int guard = 0; guard < 20_000; guard++)
            {
                if (_cursor < 0 || _cursor < Ring.Tail)
                {
                    long k = Ring.LatestKey();
                    if (k < 0) return;
                    if (_cursor >= 0) { _nextDisc = true; Abandon(); }   // fell behind the ring: a gap
                    _cursor = k;
                }
                var f = Ring.Get(_cursor);
                if (f is null) return;
                _cursor++;
                if (f.Kind is FrameKind.VideoConfig or FrameKind.AudioConfig) continue;

                if (f.IsVideo)
                {
                    if (!TsMuxer.Supports(f.Config))
                    {
                        if (!_warned) log.Warn("hls: the source codec can't go into HLS (needs H.264 + AAC)", new LogField("codec", f.Config?.Codec));
                        _warned = true;
                        continue;
                    }
                    if (!ReferenceEquals(f.Config, _lastVideoCfg))
                    {
                        if (_lastVideoCfg is not null) _nextDisc = true;   // new publisher / new encoder settings
                        _lastVideoCfg = f.Config;
                        VideoCodec = f.Config!.Codec;
                    }
                    if (f.IsKey) Cut(f.Ts);
                }
                else if (f.IsAudio && f.Config?.Codec is { } ac) AudioCodec = ac;

                if (_cur is not null) _mux.Write(_cur, f);
            }
        }

        private void Cut(double ts)
        {
            long seq;
            if (_leader is null)
            {
                if (_cur is not null && ts - _curStart < MinSegmentMs) return;   // keyframes closer than that: keep going
                seq = _nextSeq;
            }
            else
            {
                seq = _leader.CutAt(ts);
                if (seq < 0) return;                                            // not a cut point of the source's
            }
            Close(ts);
            _cur = new MemoryStream(256 * 1024);
            _curStart = ts;
            _curSeq = seq;
            _curDisc = _nextDisc;
            _nextDisc = false;
            _nextSeq = seq + 1;
            _mux.WriteHeader(_cur, audio: true);
            lock (_lock)
            {
                _cuts.Add((seq, ts));
                if (_cuts.Count > 256) _cuts.RemoveRange(0, 128);
            }
        }

        private void Close(double endTs)
        {
            if (_cur is null) return;
            var wall = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Clock.NowMs - _curStart);
            var seg = new Segment(_curSeq, _curStart, endTs - _curStart, _cur.ToArray(), wall, _curDisc);
            _cur = null;
            lock (_lock)
            {
                _segments.Add(seg);
                // Keep some past the window for players still downloading the oldest listed ones.
                int keep = Settings.Window * 2 + 2;
                if (_segments.Count > keep) _segments.RemoveRange(0, _segments.Count - keep);
            }
        }

        private void Abandon() => _cur = null;

        public string Playlist()
        {
            var segs = Recent();
            var sb = new StringBuilder(512);
            double target = 1;
            foreach (var s in segs) target = Math.Max(target, Math.Ceiling(s.DurationMs / 1000));
            sb.Append("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-INDEPENDENT-SEGMENTS\n");
            sb.Append("#EXT-X-TARGETDURATION:").Append(target.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(segs.Length > 0 ? segs[0].Seq : _nextSeq).Append('\n');
            // Aim every player at the same moment: StartOffsetMs behind *now*, not behind the newest segment's end
            // (which moves in segment-sized steps, and lags further on a re-encoded variant). So the offset from the
            // playlist's end shrinks by however long ago that segment ended. At least a second: room to play.
            double end = segs.Length > 0 ? segs[^1].StartTs + segs[^1].DurationMs : Clock.NowMs;
            double offset = Math.Max(1000, Settings.StartOffsetMs - (Clock.NowMs - end));
            sb.Append("#EXT-X-START:TIME-OFFSET=-")
              .Append((offset / 1000).ToString("0.000", CultureInfo.InvariantCulture)).Append(",PRECISE=YES\n");
            foreach (var s in segs)
            {
                if (s.Discontinuity) sb.Append("#EXT-X-DISCONTINUITY\n");
                sb.Append("#EXT-X-PROGRAM-DATE-TIME:").Append(s.Wall.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("#EXTINF:").Append((s.DurationMs / 1000).ToString("0.000", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append(Name).Append('/').Append(s.Seq).Append(".ts\n");
            }
            return sb.ToString();
        }
    }

    /// <summary>Somebody's watching over HLS (keeps the low variant running).</summary>
    public void Touch() => Volatile.Write(ref _lastRequest, Clock.NowMs);

    public Variant? Find(string name)
    {
        if (name == _source.Name) return _source;
        lock (_lock) return _low is not null && name == _low.Name ? _low : null;
    }

    /// <summary>
    /// A variant's playlist. The low variant is always listed in the master (players read it once), so until its
    /// encoder is running it answers with a valid live playlist that has no segments yet: players poll it.
    /// </summary>
    public string? VariantPlaylist(string name)
    {
        if (Find(name) is { } v) return v.Playlist();
        if (name != "low" || Settings.LowKbps <= 0) return null;
        return "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n";
    }

    public string Master()
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        long srcBw = Math.Max(_source.Bandwidth(), 500_000);
        Add(sb, _source.Name, srcBw, _source.VideoCodec ?? "avc1.640028", _source.AudioCodec ?? "mp4a.40.2");
        int low = Settings.LowKbps;
        if (low > 0)
        {
            Variant? v;
            lock (_lock) v = _low;
            // Listed whenever it's configured: players read the master once, and the low variant starts on demand.
            long bw = (low + _room.Settings.Adapt.AudioKbps) * 1000L;
            Add(sb, "low", bw, v?.VideoCodec ?? "avc1.640015", v?.AudioCodec ?? "mp4a.40.2");
        }
        return sb.ToString();

        static void Add(StringBuilder sb, string name, long bw, string video, string audio) =>
            sb.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(bw).Append(",CODECS=\"").Append(video).Append(',').Append(audio)
              .Append("\"\n").Append(name).Append(".m3u8\n");
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var next = _room.Ring.NextAppend;
                _source.Settings = Settings;
                _source.Pump(_log);
                Variant? low;
                lock (_lock) low = _low;
                if (low is not null) { low.Settings = Settings; low.Pump(_log); }
                ManageLow();
                await Task.WhenAny(next, Task.Delay(100, ct)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.Error("hls: segmenter failed", new LogField("error", e.ToString())); }
    }

    /// <summary>The low variant runs while anyone has asked for HLS in the last minute (and the stream is live).</summary>
    private void ManageLow()
    {
        double now = Clock.NowMs;
        bool wanted = Settings.LowKbps > 0 && _room.Live && now - Volatile.Read(ref _lastRequest) < LowIdleStopMs;
        if (_lowRendition is { Failed: true } || _lowRendition is not null && (!wanted || _lowRendition.Kbps != Settings.LowKbps))
        {
            StopLow(failed: _lowRendition.Failed);
        }
        if (wanted && _lowRendition is null && now >= _lowRetryAt)
        {
            try
            {
                var r = _room.StartRendition(Settings.LowKbps);
                _lowRendition = r;
                lock (_lock) _low = new Variant("low", r.Ring, _source, Settings);
                _room.ExternalKbps = r.Kbps + _room.Settings.Adapt.AudioKbps;
                _log.Info("hls: low variant started", new LogField("rendition", r.Label));
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log.Warn("hls: cannot start the low variant", new LogField("error", e.Message));
                _lowRetryAt = now + LowRetryMs;
            }
        }
    }

    private void StopLow(bool failed)
    {
        var r = _lowRendition!;
        _lowRendition = null;
        lock (_lock) _low = null;
        _room.ExternalKbps = 0;
        if (failed) _lowRetryAt = Clock.NowMs + LowRetryMs;
        _room.StopRendition(r);
        _log.Info(failed ? "hls: low variant failed" : "hls: low variant stopped", new LogField("rendition", r.Label));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop.ConfigureAwait(false);
        if (_lowRendition is not null) StopLow(failed: false);
        _stop.Dispose();
    }
}
