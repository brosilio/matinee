// WebCodecs player. Every frame carries a timestamp on the server's clock; we play media time
//   playhead = m + (serverNow - w) * r - extra
// where {w, m, r} is the room timeline from the server and `extra` is this client's own added delay (see below).
// Because every client computes the same playhead from a synced clock, clients can't drift apart.
//
// Late data is dropped, never waited for: a frame whose time has passed is not shown, and audio that is late is
// skipped. After a stall we're back at the live edge as soon as data flows again.
//
// `extra`: if this client's network can't deliver within the room delay, it adds its own delay (up to the
// server's max_delay) instead of showing nothing, and gives it back slowly once the network improves.

const KIND_VIDEO = 1, KIND_AUDIO = 2, KIND_VCONFIG = 3, KIND_ACONFIG = 4;
const MAX_QUEUED_FRAMES = 16;
const MAX_VIDEO_CHUNKS = 4096, MAX_AUDIO_CHUNKS = 8192, MAX_AUDIO_BUFFERS = 256;
const VIDEO_DECODE_AHEAD_MS = 250, AUDIO_DECODE_AHEAD_MS = 500, AUDIO_SCHEDULE_AHEAD_S = 0.1;
const EXTRA_MARGIN_MS = 150;

export class Player {
  constructor(canvas, clock) {
    this.canvas = canvas;
    this.ctx2d = canvas.getContext('2d', { alpha: false, desynchronized: true });
    this.clock = clock;
    this.tl = { w: 0, m: -1000, r: 1 };
    this.delay = 1000;
    this.maxDelay = 5000;
    this.syncMode = false;
    this.extra = 0;
    this.minSlack = Infinity;     // smallest arrival slack seen in the current window
    this.slackWindowStart = 0;

    this.vdec = null; this.vcfg = null; this.needKey = true;
    this.vchunks = [];            // compressed frames; cheap enough to hold while the shared room is paused
    this.frames = [];             // decoded VideoFrames awaiting their time, in timestamp order
    this.adec = null; this.acfg = null;
    this.achunks = [];            // compressed audio waiting to approach the playhead
    this.audio = [];              // decoded AudioBuffers waiting for the short WebAudio scheduling horizon
    this.sources = new Set();
    this.actx = null; this.gain = null; this.nextAudio = 0;
    this.volume = 1;

    this.lastRxTs = NaN;
    this.stats = { rx: 0, decoded: 0, drawn: 0, late: 0, dropped: 0, audioLate: 0, bytes: 0, errors: 0 };
    this.onStats = null;
    this.hasPicture = false;

    this.raf = requestAnimationFrame(() => this.render());
  }

  /** Must be called from a user gesture (the login click), or the browser keeps audio suspended. */
  unlockAudio() {
    if (!this.actx) {
      this.actx = new AudioContext({ latencyHint: 'interactive' });
      this.gain = this.actx.createGain();
      this.gain.gain.value = this.volume;
      this.gain.connect(this.actx.destination);
    }
    if (this.actx.state === 'suspended') this.actx.resume();
  }

  setVolume(v) {
    this.volume = v;
    if (this.gain) this.gain.gain.value = v;
  }

  setTimeline(tl) {
    const changed = this.tl.w !== tl.w || this.tl.m !== tl.m || this.tl.r !== tl.r;
    this.tl = tl;
    if (changed) {
      // WebAudio sources cannot be retimed after start. We schedule only 100 ms ahead, so discarding that tiny
      // frontier gives pause/resume and 1.05x catch-up precise behaviour without holding decoded PCM for seconds.
      this.stopAudioSources();
      this.nextAudio = 0;
    }
  }

  setDelays(delay, maxDelay, mode = 'live') {
    this.delay = delay;
    this.maxDelay = maxDelay;
    this.syncMode = mode === 'sync';
    if (this.syncMode) this.extra = 0;
    this.extra = Math.min(this.extra, Math.max(0, maxDelay - delay));
  }

  /** Media time that should be on screen right now. */
  playhead(serverNow = this.clock.now()) {
    const t = this.tl;
    return t.m + (serverNow - t.w) * t.r - this.extra;
  }

  /** How much media we hold ahead of the playhead (ms). */
  bufferedAhead() {
    return Number.isFinite(this.lastRxTs) ? this.lastRxTs - this.playhead() : 0;
  }

  /** Stream restarted or we reconnected: forget decoder state, wait for fresh configs and a keyframe. */
  reset() {
    this.needKey = true;
    this.vcfg = null; this.acfg = null;
    this.closeDecoders();
    this.dropFrames();
    this.vchunks.length = 0;
    this.achunks.length = 0;
    this.audio.length = 0;
    this.stopAudioSources();
    this.nextAudio = 0;
    this.lastRxTs = NaN;
    this.hasPicture = false;
  }

  closeDecoders() {
    for (const d of [this.vdec, this.adec]) {
      if (d && d.state !== 'closed') { try { d.close(); } catch { } }
    }
    this.vdec = null; this.adec = null;
  }

  dropFrames() {
    for (const f of this.frames) f.close();
    this.frames.length = 0;
  }

  onBinary(buf) {
    if (buf.byteLength < 12) return;
    const dv = new DataView(buf);
    const kind = dv.getUint8(0);
    const key = (dv.getUint8(1) & 1) !== 0;
    const ts = dv.getFloat64(4, true);
    const payload = new Uint8Array(buf, 12);
    this.stats.rx++;
    this.stats.bytes += buf.byteLength;

    switch (kind) {
      case KIND_VCONFIG: return this.configVideo(parseConfig(payload));
      case KIND_ACONFIG: return this.configAudio(parseConfig(payload));
      case KIND_VIDEO: this.arrived(ts); return this.queueVideo(key, ts, payload);
      case KIND_AUDIO: this.arrived(ts); return this.queueAudio(ts, payload);
    }
  }

  // Track how early media arrives relative to its play time, and adapt `extra`.
  arrived(ts) {
    if (!(ts <= this.lastRxTs)) this.lastRxTs = ts;
    if (!this.clock.synced) return;
    const now = performance.now();
    const slack = ts - this.playhead();
    // Sync mode uses the room-wide pause mechanism; private delay would make this viewer drift from everyone else
    // and would hide its low buffer from the server.
    const room = this.syncMode ? 0 : Math.max(0, this.maxDelay - this.delay);
    if (slack < 30 && this.extra < room) {
      // Arriving late: take more delay right away (a short freeze beats a black screen).
      this.extra = Math.min(room, this.extra + (EXTRA_MARGIN_MS - slack));
      this.minSlack = Infinity;
      this.slackWindowStart = now;
      return;
    }
    this.minSlack = Math.min(this.minSlack, slack);
    if (now - this.slackWindowStart > 5000) {
      // A whole window with plenty of slack: give some delay back.
      if (this.extra > 0 && this.minSlack > EXTRA_MARGIN_MS * 2) this.extra = Math.max(0, this.extra - 100);
      this.minSlack = Infinity;
      this.slackWindowStart = now;
    }
  }

  // ---- Video ----

  configVideo(c) {
    if (!c || !('VideoDecoder' in window)) return;
    if (this.vcfg && this.vcfg.codec === c.codec && sameBytes(this.vcfg.description, c.description)
        && this.vdec && this.vdec.state === 'configured') return;
    this.vcfg = c;
    this.openVideo();
  }

  openVideo() {
    const c = this.vcfg;
    if (this.vdec && this.vdec.state !== 'closed') { try { this.vdec.close(); } catch { } }
    this.dropFrames();
    this.vchunks.length = 0;
    this.needKey = true;
    const cfg = { codec: c.codec, optimizeForLatency: true };
    if (c.description.byteLength) cfg.description = c.description;
    const dec = new VideoDecoder({
      output: (frame) => this.onVideoFrame(frame),
      error: (e) => {
        console.warn('video decoder error', e);
        this.stats.errors++;
        if (this.vdec === dec) { this.vdec = null; setTimeout(() => this.vcfg && this.openVideo(), 100); }
      },
    });
    try {
      dec.configure(cfg);
      this.vdec = dec;
    } catch (e) {
      console.warn('video config rejected', cfg.codec, e);
      this.stats.errors++;
    }
  }

  decodeVideo(key, ts, data) {
    const dec = this.vdec;
    if (!dec || dec.state !== 'configured') return;
    if (this.needKey && !key) { this.stats.dropped++; return; }
    if (dec.decodeQueueSize > 20) {
      // The decoder can't keep up (slow machine, background tab): skip to the next keyframe.
      this.needKey = true;
      this.stats.dropped++;
      return;
    }
    // A frame that is already far too late is still decoded (later frames reference it) unless it's a keyframe
    // boundary we can skip to; the renderer drops it.
    this.needKey = false;
    try {
      dec.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: Math.round(ts * 1000), data }));
    } catch (e) {
      this.needKey = true;
      this.stats.errors++;
    }
  }

  queueVideo(key, ts, data) {
    this.vchunks.push({ key, ts, data });
    if (this.vchunks.length <= MAX_VIDEO_CHUNKS) return;

    // Bound hostile or wildly-ahead input without resuming between predictive frames: retain the newest keyframe.
    let keep = -1;
    for (let i = this.vchunks.length - 1; i >= 0; i--) {
      if (this.vchunks[i].key) { keep = i; break; }
    }
    const n = keep > 0 ? keep : 1;
    this.vchunks.splice(0, n);
    this.stats.dropped += n;
    this.needKey = true;
  }

  pumpVideo() {
    const dec = this.vdec;
    if (!dec || dec.state !== 'configured') return;
    const horizon = this.playhead() + VIDEO_DECODE_AHEAD_MS;
    while (this.vchunks.length && dec.decodeQueueSize <= 8) {
      const c = this.vchunks[0];
      if (c.ts > horizon) break;
      this.vchunks.shift();
      this.decodeVideo(c.key, c.ts, c.data);
    }
  }

  onVideoFrame(frame) {
    this.stats.decoded++;
    const ts = frame.timestamp / 1000;
    if (ts < this.playhead() - 500) { frame.close(); this.stats.late++; return; }
    // Insert in timestamp order (B-frames come out in order anyway; this is cheap insurance).
    let i = this.frames.length;
    while (i > 0 && this.frames[i - 1].timestamp > frame.timestamp) i--;
    this.frames.splice(i, 0, frame);
    while (this.frames.length > MAX_QUEUED_FRAMES) { this.frames.shift().close(); this.stats.dropped++; }
  }

  render() {
    this.raf = requestAnimationFrame(() => this.render());
    this.pumpVideo();
    this.pumpAudio();
    this.scheduleAudio();
    if (!this.frames.length) return;
    const ph = this.playhead();
    // The newest frame that is due; everything before it is stale.
    let due = -1;
    for (let i = 0; i < this.frames.length; i++) {
      if (this.frames[i].timestamp / 1000 <= ph + 8) due = i; else break;
    }
    if (due < 0) return;
    for (let i = 0; i < due; i++) { this.frames[i].close(); this.stats.late++; }
    const f = this.frames[due];
    this.frames.splice(0, due + 1);
    if (this.canvas.width !== f.displayWidth || this.canvas.height !== f.displayHeight) {
      this.canvas.width = f.displayWidth;
      this.canvas.height = f.displayHeight;
    }
    this.ctx2d.drawImage(f, 0, 0, f.displayWidth, f.displayHeight);
    f.close();
    this.stats.drawn++;
    this.hasPicture = true;
  }

  // ---- Audio ----

  configAudio(c) {
    if (!c || !('AudioDecoder' in window)) return;
    if (this.acfg && this.acfg.codec === c.codec && sameBytes(this.acfg.description, c.description)
        && this.adec && this.adec.state === 'configured') return;
    this.acfg = c;
    this.openAudio();
  }

  openAudio() {
    const c = this.acfg;
    if (this.adec && this.adec.state !== 'closed') { try { this.adec.close(); } catch { } }
    const cfg = { codec: c.codec, sampleRate: c.sampleRate, numberOfChannels: c.channels };
    if (c.description.byteLength && c.codec !== 'opus') cfg.description = c.description;
    const dec = new AudioDecoder({
      output: (data) => this.onAudioData(data),
      error: (e) => {
        console.warn('audio decoder error', e);
        this.stats.errors++;
        if (this.adec === dec) { this.adec = null; setTimeout(() => this.acfg && this.openAudio(), 100); }
      },
    });
    try {
      dec.configure(cfg);
      this.adec = dec;
    } catch (e) {
      console.warn('audio config rejected', cfg.codec, e);
      this.stats.errors++;
    }
    this.achunks.length = 0;
    this.audio.length = 0;
    this.stopAudioSources();
    this.nextAudio = 0;
  }

  decodeAudio(ts, data) {
    const dec = this.adec;
    if (!dec || dec.state !== 'configured' || !this.actx) return;
    if (dec.decodeQueueSize > 50) { this.stats.audioLate++; return; }
    // Too late to ever be heard: don't bother decoding.
    if (ts < this.playhead() - 200) { this.stats.audioLate++; return; }
    try {
      dec.decode(new EncodedAudioChunk({ type: 'key', timestamp: Math.round(ts * 1000), data }));
    } catch (e) {
      this.stats.errors++;
    }
  }

  queueAudio(ts, data) {
    this.achunks.push({ ts, data });
    if (this.achunks.length > MAX_AUDIO_CHUNKS) {
      this.achunks.shift();
      this.stats.audioLate++;
    }
  }

  pumpAudio() {
    const dec = this.adec;
    if (!dec || dec.state !== 'configured' || !this.actx) return;
    const horizon = this.playhead() + AUDIO_DECODE_AHEAD_MS;
    while (this.achunks.length && dec.decodeQueueSize <= 20) {
      const c = this.achunks[0];
      if (c.ts > horizon) break;
      this.achunks.shift();
      this.decodeAudio(c.ts, c.data);
    }
  }

  onAudioData(ad) {
    const actx = this.actx;
    if (!actx || actx.state !== 'running') { ad.close(); return; }
    const n = ad.numberOfFrames, ch = ad.numberOfChannels, rate = ad.sampleRate;
    const buf = actx.createBuffer(ch, n, rate);
    for (let c = 0; c < ch; c++) {
      const dst = new Float32Array(n);
      ad.copyTo(dst, { planeIndex: c, format: 'f32-planar' });
      buf.copyToChannel(dst, c);
    }
    const ts = ad.timestamp / 1000;
    ad.close();

    let i = this.audio.length;
    while (i > 0 && this.audio[i - 1].ts > ts) i--;
    this.audio.splice(i, 0, { ts, buf });
    if (this.audio.length > MAX_AUDIO_BUFFERS) {
      this.audio.shift();
      this.stats.audioLate++;
    }
  }

  scheduleAudio() {
    const actx = this.actx;
    if (!actx || actx.state !== 'running') return;
    const r = this.tl.r;
    if (r <= 0) return;                                      // room is paused
    while (this.audio.length) {
      const { ts, buf } = this.audio[0];
      const ctxNow = actx.currentTime;
      const lat = actx.outputLatency || actx.baseLatency || 0;
      const ideal = ctxNow + (ts - this.playhead()) / 1000 / r - lat;
      if (ideal > ctxNow + AUDIO_SCHEDULE_AHEAD_S) break;
      this.audio.shift();

      const dur = buf.duration / r;
      // Keep consecutive chunks back to back (no clicks) unless we're off by more than 40 ms.
      let start = this.nextAudio && Math.abs(ideal - this.nextAudio) < 0.04 ? this.nextAudio : ideal;
      let offset = 0;
      if (start < ctxNow + 0.005) {
        offset = (ctxNow + 0.005 - start) * r;
        if (offset >= buf.duration) { this.stats.audioLate++; this.nextAudio = 0; continue; }
        start = ctxNow + 0.005;
      }
      const src = actx.createBufferSource();
      src.buffer = buf;
      src.playbackRate.value = r;
      src.connect(this.gain);
      src.onended = () => this.sources.delete(src);
      this.sources.add(src);
      src.start(start, offset);
      this.nextAudio = start + dur - offset / r;
    }
  }

  stopAudioSources() {
    for (const src of this.sources) {
      try { src.stop(); } catch { }
    }
    this.sources.clear();
  }
}

function parseConfig(p) {
  if (p.byteLength < 6) return null;
  const n = p[0];
  if (p.byteLength < 1 + n + 5) return null;
  const codec = new TextDecoder().decode(p.subarray(1, 1 + n));
  const dv = new DataView(p.buffer, p.byteOffset, p.byteLength);
  const sampleRate = dv.getUint32(1 + n, true);
  const channels = p[1 + n + 4];
  const description = p.slice(1 + n + 5);   // copy: the decoder keeps it
  return { codec, sampleRate, channels, description };
}

function sameBytes(a, b) {
  if (a.byteLength !== b.byteLength) return false;
  for (let i = 0; i < a.byteLength; i++) if (a[i] !== b[i]) return false;
  return true;
}
