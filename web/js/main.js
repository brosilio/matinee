import { Connection } from './conn.js';
import { Player } from './player.js';
import { Chat } from './chat.js';

// Where the server is: ?server=wss://host/ws, or window.MATINEE_SERVER (set in config.js when this client
// is hosted somewhere other than the server), or the page's own origin.
function serverUrl() {
  const q = new URLSearchParams(location.search).get('server');
  if (q) return q;
  if (window.MATINEE_SERVER) return window.MATINEE_SERVER;
  // Relative to the page, so the client works under a path too (https://example.com/matinee/ -> /matinee/ws).
  const u = new URL('ws', location.href);
  u.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return u.href;
}

const store = {
  get(k, s = localStorage) { try { return s.getItem(k); } catch { return null; } },
  set(k, v, s = localStorage) { try { s.setItem(k, v); } catch { } },
};

function token() {
  // Per tab: lets a reconnect take over this tab's stale connection (and its nick).
  let t = store.get('tok', sessionStorage);
  if (!t) {
    const b = new Uint8Array(16);
    crypto.getRandomValues(b);
    t = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
    store.set('tok', t, sessionStorage);
  }
  return t;
}

const $ = (s) => document.querySelector(s);
const gate = $('#gate'), gateForm = $('#gate form'), gateErr = $('#gate .err');
const overlay = $('#overlay'), statsEl = $('#stats');

let conn = null, player = null, chat = null, live = false, connState = 'idle';

$('#nick').value = store.get('nick') || '';
$('#pass').value = store.get('pass', sessionStorage) || '';

if (!('VideoDecoder' in window)) {
  gateErr.textContent = 'This browser has no WebCodecs. Use a recent Firefox or Chrome.';
}

gateForm.addEventListener('submit', (e) => {
  e.preventDefault();
  const nick = $('#nick').value.trim().replace(/^@/, '');
  const pass = $('#pass').value;
  if (!nick) return;
  store.set('nick', nick);
  store.set('pass', pass, sessionStorage);
  gateErr.textContent = '';
  start(nick, pass);
});

function start(nick, pass) {
  if (!player) player = new Player($('#screen'), null);
  player.unlockAudio();   // we're inside the submit click: audio may start
  if (!chat) chat = new Chat($('#chat'), (m) => conn && conn.send(m));

  conn?.stop();
  conn = new Connection(serverUrl(), () => ({ nick, pass, tok: token() }), {
    onState: (s, detail) => {
      connState = s;
      if (s === 'open') {
        gate.hidden = true;
        player.tier = null;   // a new connection starts on the source
        chat.setMe(detail.nick);
        player.setDelays(detail.delay, detail.maxDelay, detail.mode);
        player.reset();
        chat.input.focus();
      }
      updateOverlay(detail);
    },
    onFatal: (code, text) => {
      gate.hidden = false;
      gateErr.textContent = text;
      player.reset();
      updateOverlay();
    },
    onJson: (m) => {
      switch (m.t) {
        case 'tl': player.setTimeline({ w: m.w, m: m.m, r: m.r }); break;
        case 'status':
          if (m.live && !live) player.reset();
          live = m.live;
          $('#viewers').textContent = `${m.viewers} watching`;
          $('#viewers').title = (m.who || []).join(', ');
          updateOverlay();
          break;
        case 'hist': chat.setHistory(m.l); break;
        case 'line': chat.addLine(m); break;
        case 'floor': chat.setFloor(m); break;
        case 'took': chat.onTook(m.x); break;
        case 'tier': player.tier = m.name; break;   // which rendition we're watching (source, or a re-encode)
      }
    },
    onBinary: (buf) => player.onBinary(buf),
    pingExtra: () => {
      const a = player.lastRxTs;
      if (!Number.isFinite(a)) return {};
      // Ack immediately, but don't drive the shared sync policy until this tab has a valid server clock.
      return conn.clock.synced ? { a, b: Math.round(player.bufferedAhead()) } : { a };
    },
  });
  player.clock = conn.clock;
  conn.start();
}

function updateOverlay(detail) {
  let text = '';
  if (connState === 'connecting') text = 'connecting…';
  else if (connState === 'reconnecting') text = 'reconnecting…';
  else if (connState === 'waiting') text = typeof detail === 'string' ? detail : 'waiting…';
  else if (connState === 'open' && !live) text = 'waiting for the stream to start';
  else if (connState === 'open' && player && !player.hasPicture) text = 'tuning in…';
  overlay.textContent = text;
  overlay.hidden = !text;
}

// Stats and the "tuning in" overlay refresh.
setInterval(() => {
  if (!player || !conn) return;
  if (overlay.textContent === 'tuning in…' && player.hasPicture) updateOverlay();
  if (statsEl.hidden) return;
  const s = player.stats;
  statsEl.textContent =
    `rtt ${conn.clock.rtt.toFixed(0)}ms · ahead ${player.bufferedAhead().toFixed(0)}ms · extra delay ${player.extra.toFixed(0)}ms\n` +
    `stream ${player.tier || 'source'}\n` +
    `frames rx ${s.rx} decoded ${s.decoded} drawn ${s.drawn} late ${s.late} dropped ${s.dropped} audio-late ${s.audioLate} errors ${s.errors}\n` +
    `${(s.bytes / 1048576).toFixed(1)} MiB`;
}, 500);

$('#btn-stats').addEventListener('click', () => { statsEl.hidden = !statsEl.hidden; });
$('#btn-full').addEventListener('click', () => {
  if (document.fullscreenElement) document.exitFullscreen();
  else $('#stage').requestFullscreen?.();
});
$('#volume').value = store.get('vol') ?? '1';
$('#volume').addEventListener('input', (e) => {
  const v = Number(e.target.value);
  store.set('vol', String(v));
  player?.setVolume(v);
});
document.addEventListener('click', () => player?.unlockAudio());
