// Movie-theater chat. The server owns the floor (see Floor.cs); this is the view and the input protocol:
//   - space completes a word: send the whole input as {k:'word'}; the first to complete a word takes the floor
//   - while we hold the floor, every correction streams as {k:'edit'}
//   - Enter sends {k:'send'}; refused (silently kept in our box) if someone else holds the floor
//   - {t:'took', x} means x left our box and was said: strip it

const MAX_LINES = 500;

export class Chat {
  constructor(root, send) {
    this.send = send;
    this.me = null;
    this.floor = { o: null, x: '', c: false, ty: [], w: [] };
    this.typing = false;

    this.log = root.querySelector('.chat-log');
    this.liveLine = root.querySelector('.chat-live');
    this.input = root.querySelector('.chat-input');
    this.status = root.querySelector('.chat-status');
    this.hint = root.querySelector('.chat-hint');

    this.lastSent = '';
    this.editPending = false;
    this.input.addEventListener('input', () => this.onInput());
    this.input.addEventListener('keydown', (e) => this.onKey(e));
    root.querySelector('.chat-box').addEventListener('click', () => this.input.focus());
  }

  setMe(nick) { this.me = nick; }

  ownsFloor() { return this.me !== null && this.floor.o === this.me; }

  onKey(e) {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      const x = this.input.value;
      if (!x.trim()) return;
      if (this.floor.o && !this.ownsFloor()) {
        this.flashHint(`@${this.floor.o} has the floor. Wait for quiet, then press Enter.`);
      }
      this.send({ t: 'in', k: 'send', x });
    }
  }

  onInput() {
    const x = this.input.value;
    const typing = x.trim().length > 0;
    if (typing !== this.typing) {
      this.typing = typing;
      this.send({ t: 'type', on: typing });
    }
    if (words(x) > words(this.lastSent)) {
      this.send({ t: 'in', k: 'word', x });
    } else if (this.ownsFloor()) {
      // Corrections stream by character, coalesced per frame.
      if (!this.editPending) {
        this.editPending = true;
        requestAnimationFrame(() => {
          this.editPending = false;
          if (this.ownsFloor()) this.send({ t: 'in', k: 'edit', x: this.input.value });
        });
      }
    }
    this.lastSent = x;
  }

  onTook(x) {
    const v = this.input.value;
    const t = v.trimStart();
    if (t.startsWith(x)) {
      this.input.value = t.slice(x.length).trimStart();
      this.lastSent = this.input.value;
      const typing = this.input.value.trim().length > 0;
      if (typing !== this.typing) {
        this.typing = typing;
        this.send({ t: 'type', on: typing });
      }
    }
  }

  setFloor(f) {
    this.floor = f;
    this.renderLive();
    this.renderStatus();
  }

  setHistory(lines) {
    this.log.textContent = '';
    for (const l of lines) this.appendLine(l, false);
    this.scroll();
  }

  addLine(l) {
    this.appendLine(l, true);
  }

  appendLine(l, scroll) {
    const stick = this.atBottom();
    const div = document.createElement('div');
    div.className = 'line ' + (l.k === 'sys' ? 'sys' : l.k === 'clash' ? 'clash' : 'say');
    if (l.n && l.n === this.me) div.classList.add('mine');
    if (l.k === 'sys') {
      div.textContent = '* ' + l.x;
    } else {
      const who = document.createElement('span');
      who.className = 'who';
      who.textContent = l.n;
      who.style.color = nickColor(l.n);
      div.append(who, document.createTextNode(' ' + l.x));
    }
    this.log.append(div);
    while (this.log.childElementCount > MAX_LINES) this.log.firstElementChild.remove();
    if (scroll && stick) this.scroll();
  }

  renderLive() {
    const f = this.floor;
    const stick = this.atBottom();
    this.liveLine.textContent = '';
    if (f.o && !f.c && f.x) {
      const who = document.createElement('span');
      who.className = 'who';
      who.textContent = f.o;
      who.style.color = nickColor(f.o);
      const caret = document.createElement('span');
      caret.className = 'caret';
      this.liveLine.append(who, document.createTextNode(' ' + f.x), caret);
      this.liveLine.hidden = false;
    } else {
      this.liveLine.hidden = true;
    }
    this.input.classList.toggle('holding', this.ownsFloor());
    this.input.classList.toggle('waiting', !!f.o && !this.ownsFloor() && this.input.value.trim().length > 0);
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
    const box = this.log.parentElement;
    return box.scrollHeight - box.scrollTop - box.clientHeight < 40;
  }

  scroll() {
    const box = this.log.parentElement;
    box.scrollTop = box.scrollHeight;
  }
}

function words(x) {
  // Completed words: those followed by whitespace.
  let n = 0, inWord = false;
  for (const ch of x) {
    if (/\s/.test(ch)) { if (inWord) n++; inWord = false; } else inWord = true;
  }
  return n;
}

function nickColor(nick) {
  let h = 0;
  for (const ch of nick) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return `hsl(${h % 360} 70% 70%)`;
}
