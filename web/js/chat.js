// Movie-theater chat. The server owns the floor (see Floor.cs); this is the view and the input protocol.
//
// Words, not characters, are the unit of speech. A word exists once its space is typed:
//   - the first person to complete a word takes the floor; every completed word goes out as {k:'word'}
//   - words already out stay out: corrections to them stream per character as {k:'edit'}, even backspacing
//     through the space after the last one
//   - the word being typed is never sent; Enter sends everything as {k:'send'}
//   - a line stays live until Enter, unless someone interrupts after we've been quiet a while: then it's said as
//     it stands, and whatever we hadn't sent stays in the box ({t:'took', x}: x left our box and was said)
//   - typing heartbeats ({t:'type'}) tell the server we're still talking, even in the middle of a long word
//
// You type straight into the history: your line is the last line, laid out exactly like the lines above it. Each
// word shows its state: typing (not sent yet), held (someone else has the floor), on air (the server has it,
// with a short flash when that's confirmed). When you commit, the line simply becomes history.

const MAX_LINES = 500;
const ACK_MS = 600, FADE_MS = 350;

export class Chat {
  constructor(root, send) {
    this.send = send;
    this.me = null;
    this.floor = { o: null, x: '', c: false, ty: [], w: [] };
    this.typing = false;
    this.lastBeat = 0;
    this.onAir = 0;              // how many of our words are out (on the live line, or contending for it)
    this.lastBroadcast = '';
    this.acked = new Map();      // index of my word -> when it was confirmed on air
    this.fresh = new Map();      // index of a word in someone else's live line -> when it appeared
    this.liveOwner = null;
    this.liveWords = [];

    this.box = root.querySelector('.chat-box');
    this.log = root.querySelector('.chat-log');
    this.liveLine = root.querySelector('.chat-live');
    this.meLine = root.querySelector('.chat-me');
    this.mirror = root.querySelector('.chat-mirror');
    this.input = root.querySelector('.chat-input');
    this.status = root.querySelector('.chat-status');
    this.hint = root.querySelector('.chat-hint');

    this.input.addEventListener('input', () => this.onInput());
    this.input.addEventListener('keydown', (e) => this.onKey(e));
    this.box.addEventListener('click', (e) => {
      if (window.getSelection()?.toString()) return;   // let people select history text
      if (e.target !== this.input) this.input.focus();
    });
    window.addEventListener('resize', () => this.renderMine());
  }

  setMe(nick) {
    this.me = nick;
    this.meLine.hidden = false;
    this.renderMine();
  }

  ownsFloor() { return this.me !== null && this.floor.o === this.me; }

  onKey(e) {
    if (e.key !== 'Enter' || e.isComposing) return;
    e.preventDefault();                       // one line, always
    const x = this.input.value;
    if (!x.trim()) return;
    if (this.floor.o && !this.ownsFloor()) {
      this.flashHint(`@${this.floor.o} is talking. Once they pause, Enter (or a finished word) cuts in.`);
    }
    this.send({ t: 'in', k: 'send', x });
  }

  onInput() {
    if (/[\r\n]/.test(this.input.value)) {    // pasted newlines
      const pos = this.input.selectionStart;
      this.input.value = this.input.value.replace(/[\r\n]+/g, ' ');
      this.input.setSelectionRange(pos, pos);
    }
    const x = this.input.value;
    this.setTyping(x.trim().length > 0, true);

    const complete = completePart(x);
    const done = words(complete).length;
    const toks = wordSpans(x);
    if (done > this.onAir) {
      // New words: out they go.
      this.onAir = done;
      this.lastBroadcast = complete;
      this.send({ t: 'in', k: 'word', x: complete });
    } else {
      // Corrections inside words that are already out (the server won't let an edit add a word).
      this.onAir = Math.min(this.onAir, toks.length);
      if (this.ownsFloor() && this.onAir > 0) {
        let end = toks[this.onAir - 1][1];
        while (end < x.length && /\s/.test(x[end])) end++;
        const b = x.slice(0, end);
        if (b !== this.lastBroadcast) {
          this.lastBroadcast = b;
          this.send({ t: 'in', k: 'edit', x: b });
        }
      }
    }
    this.renderMine();
  }

  /** Typing indicator, plus a heartbeat at most once a second while keys are going. */
  setTyping(on, keystroke = false) {
    const now = performance.now();
    if (on === this.typing && !(on && keystroke && now - this.lastBeat > 1000)) return;
    this.typing = on;
    this.lastBeat = now;
    this.send({ t: 'type', on });
  }

  onTook(x) {
    const t = this.input.value.trimStart();
    if (!t.startsWith(x)) return;
    this.input.value = t.slice(x.length).trimStart();
    this.input.setSelectionRange(this.input.value.length, this.input.value.length);
    this.onAir = 0;
    this.lastBroadcast = '';
    this.acked.clear();
    this.setTyping(this.input.value.trim().length > 0);
    this.renderMine();
  }

  setFloor(f) {
    this.floor = f;
    this.renderLive();
    this.renderMine();
    this.renderStatus();
  }

  setHistory(lines) {
    this.log.textContent = '';
    for (const l of lines) this.appendLine(l);
    this.scroll();
  }

  addLine(l) {
    const stick = this.atBottom();
    this.appendLine(l);
    if (stick) this.scroll();
  }

  appendLine(l) {
    const div = document.createElement('div');
    div.className = 'line ' + (l.k === 'sys' ? 'sys' : l.k === 'clash' ? 'clash' : 'say');
    if (l.n && l.n === this.me) div.classList.add('mine');
    if (l.k === 'sys') div.textContent = '* ' + l.x;
    else div.append(whoSpan(l.n), document.createTextNode(l.x));
    this.log.append(div);
    while (this.log.childElementCount > MAX_LINES) this.log.firstElementChild.remove();
  }

  // Someone else's line as it's being said. New words fade in; corrected words just change.
  renderLive() {
    const f = this.floor;
    const stick = this.atBottom();
    const show = f.o && f.o !== this.me && !f.c && f.x.trim();
    if (!show) {
      this.liveLine.hidden = true;
      this.liveOwner = null;
      this.liveWords = [];
      this.fresh.clear();
      if (stick) this.scroll();
      return;
    }
    if (f.o !== this.liveOwner) { this.liveOwner = f.o; this.liveWords = []; this.fresh.clear(); }
    const ws = words(f.x);
    const now = performance.now();
    for (let i = this.liveWords.length; i < ws.length; i++) this.fresh.set(i, now);
    this.liveWords = ws;

    this.liveLine.textContent = '';
    this.liveLine.append(whoSpan(f.o));
    ws.forEach((w, i) => {
      if (i > 0) this.liveLine.append(document.createTextNode(' '));
      const s = document.createElement('span');
      s.className = 'w';
      s.textContent = w;
      animate(s, 'fresh', this.fresh.get(i), FADE_MS, now);
      this.liveLine.append(s);
    });
    const caret = document.createElement('span');
    caret.className = 'caret';
    this.liveLine.append(caret);
    this.liveLine.hidden = false;
    if (stick) this.scroll();
  }

  // Our own line: the mirror draws what the transparent textarea holds, word by word.
  renderMine() {
    if (!this.me) return;
    const stick = this.atBottom();
    const x = this.input.value;
    const f = this.floor;
    const mine = this.ownsFloor() && !f.c;
    const onAir = mine ? words(f.x) : [];
    const heldBy = f.o && !this.ownsFloor() ? f.o : null;
    const now = performance.now();

    const m = this.mirror;
    m.textContent = '';
    const who = whoSpan(this.me);
    m.append(who);
    if (!x) {
      const p = document.createElement('span');
      p.className = 'placeholder';
      p.textContent = heldBy ? `@${heldBy} is talking…` : 'say something…';
      m.append(p);
    }
    for (const tok of x.match(/\s+|\S+/g) || []) {
      if (/^\s/.test(tok)) { m.append(document.createTextNode(tok)); continue; }
      const s = document.createElement('span');
      s.className = 'w';
      s.textContent = tok;
      m.append(s);
    }
    // Word states. A word is complete if whitespace follows it in the input.
    const spans = m.querySelectorAll('.w');
    // Words out count as complete even while being corrected (no space after them right now).
    const completeCount = Math.max(words(completePart(x)).length, this.onAir);
    let sent = 0;
    spans.forEach((s, i) => {
      let cls;
      if (i >= completeCount) cls = 'typing';
      else if (mine && i < onAir.length && onAir[i] === s.textContent) { cls = 'sent'; sent++; }
      else if (heldBy) cls = 'held';
      else cls = 'pending';
      s.classList.add(cls);
      if (cls === 'sent') {
        if (!this.acked.has(i)) this.acked.set(i, now);
        animate(s, 'ack', this.acked.get(i), ACK_MS, now);
      }
    });
    for (const i of [...this.acked.keys()]) if (i >= sent) this.acked.delete(i);
    m.append(document.createTextNode('​'));   // keeps the last (empty) line's height

    // Line up the textarea's first line after our nick, exactly like the mirror (and like history lines).
    this.input.style.textIndent = who.getBoundingClientRect().width + 'px';
    this.meLine.classList.toggle('on-air', mine);
    if (stick) this.scroll();
  }

  renderStatus() {
    const f = this.floor;
    const others = (list) => list.filter((n) => n !== this.me).map((n) => '@' + n);
    const wanting = others(f.w);
    const typing = others(f.ty);
    let s = '';
    if (f.c) s = 'everyone is talking over each other. someone has to shut up.';
    else if (wanting.length) s = `${wanting.join(',')} ${wanting.length === 1 ? 'wants' : 'want'} to say something next`;
    else if (typing.length) s = `${typing.join(',')} ${typing.length === 1 ? 'is' : 'are'} talking`;
    this.status.textContent = s || ' ';
  }

  flashHint(text) {
    this.hint.textContent = text;
    this.hint.hidden = false;
    clearTimeout(this.hintTimer);
    this.hintTimer = setTimeout(() => { this.hint.hidden = true; }, 3000);
  }

  atBottom() {
    return this.box.scrollHeight - this.box.scrollTop - this.box.clientHeight < 40;
  }

  scroll() {
    this.box.scrollTop = this.box.scrollHeight;
  }
}

function whoSpan(nick) {
  const who = document.createElement('span');
  who.className = 'who';
  who.textContent = nick + ' ';
  who.style.color = nickColor(nick);
  return who;
}

// Spans are rebuilt on every render; keep a running animation going by starting it in the past.
function animate(el, cls, since, duration, now) {
  if (since === undefined || now - since >= duration) return;
  el.classList.add(cls);
  el.style.animationDelay = `${-(now - since)}ms`;
}

/** The part of the input made of finished words (everything up to the last whitespace). */
export function completePart(x) {
  let end = x.length;
  while (end > 0 && !/\s/.test(x[end - 1])) end--;
  return x.slice(0, end);
}

/** [start, end) of each word in a text. */
function wordSpans(x) {
  const out = [];
  const re = /\S+/g;
  let m;
  while ((m = re.exec(x))) out.push([m.index, m.index + m[0].length]);
  return out;
}

/** The words of a text. */
export function words(x) {
  return x.split(/\s+/).filter(Boolean);
}

function nickColor(nick) {
  let h = 0;
  for (const ch of nick) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return `hsl(${h % 360} 70% 70%)`;
}
