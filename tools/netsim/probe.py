#!/usr/bin/env python3
"""Headless matinee viewer that measures what a real viewer would see.

It mirrors web/js/player.js timing: server clock sync (min-RTT filter), the room timeline, and the adaptive
per-client `extra` delay. It decodes nothing; for every video frame it records when it arrived relative to when it
was due on screen.

Per window it reports:
  on-time   frames that arrived before their play time
  late      arrived after their play time but would still be drawn (picture runs late briefly)
  dropped   arrived too late to draw (>500 ms), or never arrived (gaps in the stream: content missed)
  freeze    time the picture would be frozen (no new frame for >2 frame intervals)
  lag       how far behind live the viewer plays (room delay + its own extra delay)

stdlib only. Usage: probe.py [--url ws://127.0.0.1:8080/ws | wss://host/path/ws] [--seconds 30] [--window 5] [--pass popcorn]
"""
import argparse, asyncio, base64, json, os, ssl, statistics, struct, time

def now_ms():
    return time.monotonic() * 1000.0

class Clock:
    def __init__(self): self.samples, self.offset, self.synced, self.rtt = [], 0.0, False, float("nan")
    def sample(self, c0, s, c1):
        rtt = c1 - c0
        if rtt < 0 or rtt > 10000: return
        self.samples = (self.samples + [(rtt, s - (c0 + c1) / 2)])[-24:]
        best = min(self.samples)
        self.rtt = best[0]
        diff = best[1] - self.offset
        if not self.synced or abs(diff) > 100: self.offset, self.synced = best[1], True
        else: self.offset += max(-2, min(2, diff))
    def now(self): return now_ms() + self.offset

class Stats:
    def __init__(self): self.reset()
    def reset(self):
        self.on_time = self.late = self.dropped_late = 0
        self.missed_ms = 0.0
        self.freeze_ms = 0.0
        self.slacks = []
        self.bytes = 0

class Probe:
    def __init__(self, a):
        self.a = a
        self.clock = Clock()
        self.tl = (0.0, -1000.0, 1.0)
        self.delay, self.max_delay = 1000, 5000
        self.extra = 0.0
        self.min_slack, self.win_start = float("inf"), now_ms()
        self.last_rx_ts = None
        self.last_video_ts = None
        self.frame_ms = 33.3
        self.intervals = []
        self.last_display = None
        self.w = Stats()
        self.total = Stats()
        self.reconnects = 0
        self.live = False
        self.tier = "source"

    def playhead(self):
        w, m, r = self.tl
        return m + (self.clock.now() - w) * r - self.extra

    def adapt(self, ts):
        if not self.clock.synced: return
        slack = ts - self.playhead()
        room = max(0, self.max_delay - self.delay)
        t = now_ms()
        if slack < 30 and self.extra < room:
            self.extra = min(room, self.extra + (150 - slack))
            self.min_slack, self.win_start = float("inf"), t
            return
        self.min_slack = min(self.min_slack, slack)
        if t - self.win_start > 5000:
            spare = self.min_slack - 300
            if self.extra > 0 and spare > 0: self.extra = max(0, self.extra - min(500, max(100, spare / 2)))
            self.min_slack, self.win_start = float("inf"), t

    def on_video(self, ts):
        self.adapt(ts)
        if not self.clock.synced: return
        slack = ts - self.playhead()
        for s in (self.w, self.total): s.slacks.append(slack)
        # Missed content: a gap in the stream bigger than a couple of frames.
        if self.last_video_ts is not None:
            gap = ts - self.last_video_ts
            if 0 < gap < 1000: self.intervals = (self.intervals + [gap])[-60:]
            if self.intervals: self.frame_ms = statistics.median(self.intervals)
            if gap > 2.5 * self.frame_ms:
                for s in (self.w, self.total): s.missed_ms += gap - self.frame_ms
        self.last_video_ts = ts if self.last_video_ts is None else max(ts, self.last_video_ts)
        if slack <= -500:
            for s in (self.w, self.total): s.dropped_late += 1
            return
        for s in (self.w, self.total):
            if slack >= 0: s.on_time += 1
            else: s.late += 1
        # When does it reach the screen (wall clock)? Freeze = gaps between successive screen updates.
        display = now_ms() + max(0.0, slack)
        if self.last_display is not None:
            gap = display - self.last_display
            if gap > 2 * self.frame_ms:
                for s in (self.w, self.total): s.freeze_ms += gap - self.frame_ms
        self.last_display = display if self.last_display is None else max(display, self.last_display)

    async def run(self):
        end = now_ms() + self.a.seconds * 1000
        reporter = asyncio.create_task(self.report_loop())
        while now_ms() < end:
            try:
                await asyncio.wait_for(self.session(end), timeout=max(0.1, (end - now_ms()) / 1000))
            except (asyncio.TimeoutError, TimeoutError):
                break
            except (ConnectionError, OSError, asyncio.IncompleteReadError, EOFError) as e:
                pass
            if now_ms() < end:
                self.reconnects += 1
                await asyncio.sleep(0.25)
        reporter.cancel()
        self.summary()

    async def session(self, end):
        host, port, path, tls = parse(self.a.url)
        r, w = await asyncio.open_connection(host, port, ssl=ssl.create_default_context() if tls else None,
                                             server_hostname=host if tls else None)
        key = base64.b64encode(os.urandom(16)).decode()
        w.write(f"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n".encode())
        head = await r.readuntil(b"\r\n\r\n")
        if b" 101 " not in head.split(b"\r\n")[0]: raise ConnectionError(head.split(b"\r\n")[0].decode())
        send = lambda o: w.write(frame_text(json.dumps(o)))
        send({"t": "hello", "nick": self.a.nick, "pass": self.a.password, "tok": self.a.nick + "-probe-token-000"})
        self.clock = Clock()
        last_ping, last_rx = 0.0, now_ms()
        pinger_stop = asyncio.Event()

        async def pinger():
            while not pinger_stop.is_set():
                extra = {}
                if self.last_rx_ts is not None:
                    extra["a"] = self.last_rx_ts
                    if self.clock.synced: extra["b"] = round(self.last_rx_ts - self.playhead())
                send({"t": "ping", "c": now_ms(), **extra})
                await asyncio.sleep(0.3)
        ping_task = None
        try:
            while now_ms() < end:
                op, payload = await asyncio.wait_for(read_frame(r), timeout=4.0)   # client's stall detector
                if op == 8: raise ConnectionError("closed")
                if op == 1:
                    m = json.loads(payload)
                    t = m.get("t")
                    if t == "welcome":
                        self.delay, self.max_delay = m["delay"], m["maxDelay"]
                        self.extra = 0.0
                        self.last_rx_ts = None
                        if ping_task is None: ping_task = asyncio.create_task(pinger())
                    elif t == "pong": self.clock.sample(m["c"], m["s"], now_ms())
                    elif t == "tl": self.tl = (m["w"], m["m"], m["r"])
                    elif t == "status": self.live = m["live"]
                    elif t == "tier":
                        self.tier = m["name"]
                        print(f"      -> now watching: {self.tier}", flush=True)
                    elif t == "error": raise SystemExit("server refused: " + m.get("x", ""))
                elif op == 2 and len(payload) >= 12:
                    kind = payload[0]
                    ts = struct.unpack_from("<d", payload, 4)[0]
                    self.w.bytes += len(payload); self.total.bytes += len(payload)
                    if kind in (1, 2):
                        self.last_rx_ts = ts if self.last_rx_ts is None else max(ts, self.last_rx_ts)
                    if kind == 1: self.on_video(ts)
                    elif kind == 2: self.adapt(ts)
        finally:
            pinger_stop.set()
            if ping_task: ping_task.cancel()
            w.close()

    async def report_loop(self):
        t0 = now_ms()
        print(f"{'t':>5} {'on-time':>8} {'late':>6} {'dropped':>8} {'missed':>7} {'freeze':>7} "
              f"{'slack p10/p50':>14} {'lag':>6} {'rtt':>5} {'kbit/s':>7} {'rc':>3}", flush=True)
        while True:
            await asyncio.sleep(self.a.window)
            w = self.w
            self.w = Stats()
            n = w.on_time + w.late + w.dropped_late
            pct = lambda x: f"{100 * x / n:5.1f}%" if n else "    -"
            sl = sorted(w.slacks)
            p = lambda q: f"{sl[int(q * (len(sl) - 1))]:.0f}" if sl else "-"
            print(f"{(now_ms() - t0) / 1000:5.0f} {pct(w.on_time):>8} {pct(w.late):>6} {pct(w.dropped_late):>8} "
                  f"{w.missed_ms:6.0f}ms {w.freeze_ms:6.0f}ms {p(0.1) + '/' + p(0.5):>14} "
                  f"{self.delay + self.extra:5.0f}ms {self.clock.rtt:4.0f} {w.bytes * 8 / 1000 / self.a.window:7.0f} "
                  f"{self.reconnects:3d}", flush=True)

    def summary(self):
        t = self.total
        n = t.on_time + t.late + t.dropped_late
        res = {
            "frames": n,
            "on_time_pct": round(100 * t.on_time / n, 1) if n else None,
            "late_pct": round(100 * t.late / n, 1) if n else None,
            "dropped_late_pct": round(100 * t.dropped_late / n, 1) if n else None,
            "missed_ms": round(t.missed_ms),
            "freeze_ms": round(t.freeze_ms),
            "final_lag_ms": round(self.delay + self.extra),
            "reconnects": self.reconnects,
            "stream": self.tier,
        }
        print("SUMMARY " + json.dumps(res), flush=True)

def parse(url):
    tls = url.startswith("wss://")
    assert tls or url.startswith("ws://"), "ws:// or wss:// URL"
    rest = url[6:] if tls else url[5:]
    hostport, _, path = rest.partition("/")
    host, _, port = hostport.partition(":")
    return host, int(port or (443 if tls else 80)), "/" + path, tls

def frame_text(s):
    p = s.encode(); m = os.urandom(4); n = len(p)
    hdr = bytes([0x81, 0x80 | n]) if n < 126 else bytes([0x81, 0x80 | 126]) + struct.pack(">H", n)
    return hdr + m + bytes(b ^ m[i % 4] for i, b in enumerate(p))

async def read_frame(r):
    h = await r.readexactly(2)
    op, n = h[0] & 15, h[1] & 127
    if n == 126: n = struct.unpack(">H", await r.readexactly(2))[0]
    elif n == 127: n = struct.unpack(">Q", await r.readexactly(8))[0]
    return op, await r.readexactly(n)

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="ws://127.0.0.1:8080/ws")
    ap.add_argument("--seconds", type=float, default=30)
    ap.add_argument("--window", type=float, default=5)
    ap.add_argument("--nick", default="probe")
    ap.add_argument("--pass", dest="password", default="popcorn")
    asyncio.run(Probe(ap.parse_args()).run())
