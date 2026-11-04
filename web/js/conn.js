// Connection to the broadcast server: one WebSocket, reconnecting forever, with clock sync.
//
// Horrible networks are the design case:
//  - Any silence longer than STALL_MS means the connection is dead, even if the browser hasn't noticed.
//    The server answers our pings every PING_MS, so silence is never normal.
//  - Reconnects back off quickly to a short ceiling and never give up; the tab never needs a reload.
//  - The clock offset to the server is re-measured continuously (min-RTT filter), so every client agrees on
//    "now" to within a few milliseconds, whatever happened to the connection.

const PING_MS = 300;
const STALL_MS = 4000;
const BACKOFF_MIN = 250, BACKOFF_MAX = 4000;

export class ServerClock {
  constructor() { this.reset(); }

  reset() {
    this.samples = [];
    this.offset = 0;      // server time - performance.now()
    this.synced = false;
    this.rtt = NaN;
  }

  sample(c0, server, c1) {
    const rtt = c1 - c0;
    if (rtt < 0 || rtt > 10000) return;
    this.samples.push({ rtt, off: server - (c0 + c1) / 2, at: c1 });
    if (this.samples.length > 24) this.samples.shift();
    // The sample with the smallest round trip has the least queueing error.
    let best = this.samples[0];
    for (const s of this.samples) if (s.rtt < best.rtt) best = s;
    this.rtt = best.rtt;
    const diff = best.off - this.offset;
    if (!this.synced || Math.abs(diff) > 100) {
      this.offset = best.off;              // first sync, or the server restarted: jump
      this.synced = true;
    } else {
      this.offset += Math.max(-2, Math.min(2, diff));   // slew: no visible jumps
    }
  }

  now() { return performance.now() + this.offset; }
}

export class Connection {
  /**
   * @param {string} url  ws:// or wss:// URL of the server's /ws endpoint
   * @param {() => object} hello  builds the hello message (nick, pass, tok)
   * @param {object} h  handlers: onState(state, detail), onJson(msg), onBinary(ArrayBuffer), onFatal(code, text),
   *                    pingExtra() -> {a, b}
   */
  constructor(url, hello, h) {
    this.url = url;
    this.hello = hello;
    this.h = h;
    this.clock = new ServerClock();
    this.ws = null;
    this.state = 'idle';
    this.backoff = BACKOFF_MIN;
    this.lastRx = 0;
    this.stopped = false;
    this.timer = setInterval(() => this.tick(), PING_MS);
    window.addEventListener('online', () => this.reconnectNow());
    document.addEventListener('visibilitychange', () => {
      // Background tabs get throttled timers; when we come back, check the connection at once.
      if (!document.hidden) this.tick();
    });
  }

  start() {
    this.stopped = false;
    this.connect();
  }

  stop() {
    this.stopped = true;
    clearTimeout(this.retry);
    if (this.ws) { try { this.ws.close(); } catch { } }
  }

  setState(s, detail) {
    this.state = s;
    this.h.onState?.(s, detail);
  }

  connect() {
    clearTimeout(this.retry);
    if (this.stopped) return;
    this.setState('connecting');
    let ws;
    try {
      ws = new WebSocket(this.url);
    } catch (e) {
      this.scheduleReconnect(String(e));
      return;
    }
    ws.binaryType = 'arraybuffer';
    this.ws = ws;
    this.lastRx = performance.now();
    this.authed = false;

    ws.onopen = () => {
      if (ws !== this.ws) return;
      this.lastRx = performance.now();
      ws.send(JSON.stringify({ t: 'hello', ...this.hello() }));
    };
    ws.onmessage = (ev) => {
      if (ws !== this.ws) return;
      this.lastRx = performance.now();
      if (typeof ev.data === 'string') {
        let m;
        try { m = JSON.parse(ev.data); } catch { return; }
        this.onJson(m);
      } else {
        this.h.onBinary?.(ev.data);
      }
    };
    ws.onclose = (ev) => {
      if (ws !== this.ws) return;
      this.ws = null;
      if (this.fatal) return;
      this.scheduleReconnect(ev.reason || `closed (${ev.code})`);
    };
    ws.onerror = () => { /* onclose follows */ };
  }

  onJson(m) {
    switch (m.t) {
      case 'welcome':
        this.authed = true;
        this.backoff = BACKOFF_MIN;
        this.clock.reset();
        this.setState('open', m);
        this.ping();
        break;
      case 'pong':
        this.clock.sample(m.c, m.s, performance.now());
        break;
      case 'error':
        if (m.code === 'full') {
          this.setState('waiting', m.x);
          this.backoff = 10000;
          break;
        }
        this.fatal = true;
        this.stop();
        this.h.onFatal?.(m.code, m.x);
        return;
    }
    this.h.onJson?.(m);
  }

  scheduleReconnect(why) {
    if (this.stopped) return;
    const wait = this.backoff * (0.75 + Math.random() * 0.5);
    this.backoff = Math.min(BACKOFF_MAX, this.backoff * 2);
    this.setState('reconnecting', why);
    this.retry = setTimeout(() => this.connect(), wait);
  }

  reconnectNow() {
    if (this.stopped) return;
    this.backoff = BACKOFF_MIN;
    if (this.ws) { try { this.ws.close(); } catch { } this.ws = null; }
    this.connect();
  }

  tick() {
    if (!this.ws) return;
    const silent = performance.now() - this.lastRx;
    if (silent > STALL_MS) {
      // Dead or stalled connection: don't wait for TCP to notice.
      const ws = this.ws;
      this.ws = null;
      try { ws.close(); } catch { }
      this.scheduleReconnect('connection stalled');
      return;
    }
    if (this.authed) this.ping();
  }

  ping() {
    const extra = this.h.pingExtra?.() || {};
    this.send({ t: 'ping', c: performance.now(), ...extra });
  }

  send(obj) {
    const ws = this.ws;
    if (!ws || ws.readyState !== WebSocket.OPEN || !this.authed) return false;
    // Don't pile up chatter behind a congested link.
    if (ws.bufferedAmount > 64 * 1024 && obj.t === 'ping') return false;
    ws.send(JSON.stringify(obj));
    return true;
  }
}
