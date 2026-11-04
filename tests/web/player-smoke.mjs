import assert from 'node:assert/strict';

let nextAnimationFrame;
globalThis.requestAnimationFrame = (callback) => { nextAnimationFrame = callback; return 1; };
globalThis.window = globalThis;

class FakeVideoDecoder {
  static instances = [];
  constructor(init) { this.init = init; this.state = 'unconfigured'; this.decodeQueueSize = 0; this.decoded = []; FakeVideoDecoder.instances.push(this); }
  configure(config) { this.config = config; this.state = 'configured'; }
  decode(chunk) { this.decoded.push(chunk); }
  close() { this.state = 'closed'; }
}

class FakeAudioDecoder {
  constructor() { this.state = 'unconfigured'; this.decodeQueueSize = 0; }
  configure(config) { this.config = config; this.state = 'configured'; }
  decode() { }
  close() { this.state = 'closed'; }
}

globalThis.VideoDecoder = FakeVideoDecoder;
globalThis.AudioDecoder = FakeAudioDecoder;
globalThis.EncodedVideoChunk = class { constructor(init) { Object.assign(this, init); } };
globalThis.EncodedAudioChunk = class { constructor(init) { Object.assign(this, init); } };

const { Player } = await import('../../web/js/player.js');

let now = 1000;
const clock = { now: () => now, synced: true };
const canvas = {
  width: 0,
  height: 0,
  getContext: () => ({ drawImage() { } }),
};
const player = new Player(canvas, clock);
assert.equal(typeof nextAnimationFrame, 'function');

function message(kind, timestamp, payload, key = false) {
  const buf = new ArrayBuffer(12 + payload.length);
  const view = new DataView(buf);
  view.setUint8(0, kind);
  view.setUint8(1, key ? 1 : 0);
  view.setFloat64(4, timestamp, true);
  new Uint8Array(buf, 12).set(payload);
  return buf;
}

const codec = new TextEncoder().encode('avc1.42c01f');
const config = new Uint8Array(1 + codec.length + 5 + 4);
config[0] = codec.length;
config.set(codec, 1);
config.set([1, 0x42, 0xc0, 0x1f], 1 + codec.length + 5);
player.onBinary(message(3, 0, config));
assert.equal(player.vdec.state, 'configured');

// While the room is paused at media time 0, only the near-playhead compressed frame is decoded. The future frame
// stays compressed rather than becoming a large VideoFrame or being discarded.
player.setTimeline({ w: 1000, m: 0, r: 0 });
player.onBinary(message(1, 100, new Uint8Array([1]), true));
player.onBinary(message(1, 1000, new Uint8Array([2]), false));
player.render();
assert.equal(player.vdec.decoded.length, 1);
assert.equal(player.vchunks.length, 1);

now = 2000;
player.setTimeline({ w: 1000, m: 0, r: 1.05 });
player.render();
assert.equal(player.vdec.decoded.length, 2);
assert.equal(player.vchunks.length, 0);

// Sync mode never takes private delay, because that would hide a slow viewer from the shared pause policy.
player.setDelays(1000, 5000, 'sync');
player.extra = 0;
player.arrived(player.playhead() - 100);
assert.equal(player.extra, 0);

let stopped = false;
player.sources.add({ stop() { stopped = true; } });
player.setTimeline({ w: 2000, m: 1050, r: 0 });
assert.equal(stopped, true);
assert.equal(player.sources.size, 0);

console.log('player smoke: ok');
