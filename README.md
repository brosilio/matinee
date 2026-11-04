# matinee

A small, dependency-free C# live-streaming room: OBS publishes RTMP, matinee fans the encoded media out over
WebSocket, and a static WebCodecs client plays it against a server-synchronized clock. Chat is built around a
contended “movie theater” floor rather than independent messages.

The server targets .NET 10 and native AOT. Its only build-time dependency is the sibling
[`httpgrl`](../httpgrl) checkout; there are no third-party runtime libraries. Optional proxy mode launches an
installed `ffmpeg` executable.

## Build and run

```sh
dotnet build matinee.slnx -c Release
dotnet publish src/Matinee/Matinee.csproj -c Release -o publish
./publish/matinee -t -c conf/matinee.conf
./publish/matinee -c conf/matinee.conf
```

The default configuration listens for viewers on `http://localhost:8080`, accepts RTMP on port 1935, uses stream
key `change-me`, and uses viewer password `popcorn`. Change both secrets before exposing the service.

The tests have no test-framework dependency:

```sh
dotnet build matinee.slnx -c Release
dotnet tests/Matinee.Tests/bin/Release/net10.0/Matinee.Tests.dll
```

`./scripts/ci.sh` runs the JIT suite, publishes and runs the same suite as native AOT, publishes the native server,
checks the real configuration, and runs the JavaScript syntax/player smoke checks. It redirects all build artifacts
to a temporary directory so the sibling httpgrl checkout is not dirtied.

## OBS

Set the service to Custom, server to `rtmp://SERVER:1935/live`, and stream key to the configured `ingest_key`.
Use H.264 with B-frames disabled, a one-second keyframe interval, and AAC audio. Matinee also understands
Enhanced RTMP AV1 and Opus, subject to the viewer browser's WebCodecs support.

A publisher with the right key replaces the current publisher, so an OBS reconnect does not need manual cleanup.

## Streaming modes

`mode live` is ephemeral. Each viewer stays at the live edge; when its connection falls behind, video is discarded
through the next keyframe. A network hiccup loses content instead of adding permanent delay.

`mode sync` keeps at least 60 seconds of encoded media and gives the room one shared playhead. A viewer reporting
less than 500 ms buffered pauses everyone (except during its first 5 seconds after joining or a stream
restart, while its buffer is still filling). Playback resumes when it has 2 seconds, then runs at 1.05× until the
configured delay is restored. `max_delay` caps how long one viewer may hold the room; a viewer that cannot recover
is automatically reconnected at the shared playhead. These transitions are announced in chat.

The browser stores future media compressed and decodes only just ahead of the playhead. This keeps sync-mode pauses
within the server's one-core/1 GB design target and lets WebAudio stop with the shared timeline.

## Chat

One live line, contended like a room full of people talking:

- Words, not keystrokes, go on air: a word is sent when you type the space after it. The first person to finish a
  word takes the floor, and their words fade in for everyone as they're completed. Words already on air can be
  corrected, and everyone sees the correction character by character.
- A line stays live until its speaker presses Enter, however long they pause. Someone else can **interrupt** a
  speaker who's been quiet for 2.5 s by finishing a word or pressing Enter: the speaker's line is said exactly as
  it stands (a typo you didn't fix in time is yours forever), the floor changes hands, and any word the speaker
  hadn't finished stays in their box to send later.
- Anyone who types while the speaker is still going keeps their text and shows up as "wants to say something
  next". Typing counts as talking even mid-word, so nobody gets cut off halfway through a long one.
- Talk over the speaker for 8 words and it becomes a shouting match: every word from either of you becomes its own
  line until everyone stops.
- You type straight into the history: your line is the last line, laid out like the others. Its words show their
  state: dim while you're still typing them, underlined while someone else has the floor, and a short flash when
  the server confirms they're on air. When the line is done, it simply stays where it is as history.

## Configuration

All stream settings live in one top-level `matinee` block:

| Directive | Default | Meaning |
| --- | --- | --- |
| `ingest_listen [address:]port` | disabled | RTMP listener; a bare port listens on IPv4 and IPv6. |
| `ingest_key value` | none | Required publish key when ingest is enabled. |
| `password value` | none | Shared viewer password; omit for an open room. |
| `mode live\|sync` | `live` | Per-viewer live dropping or shared buffered playback. |
| `delay time` | `1s` | Target playhead distance behind ingest. |
| `max_delay time` | `5s` | Maximum client/room delay. |
| `congestion time` | `1500ms` | Unacknowledged live media before video dropping starts. |
| `buffer time` | `10s` | Encoded media retained in memory; sync mode enforces at least 60s. |
| `max_viewers number` | `50` | Simultaneous authenticated viewers. |
| `proxy_pass rtmp[s]://…` | disabled | Re-encode and publish this ingest to a remote RTMP endpoint. |
| `proxy_ffmpeg path` | `ffmpeg` | ffmpeg executable for proxy mode. |
| `proxy_args …` | x264/AAC low-latency settings | Arguments between ffmpeg's FLV input and output. |
| `adapt on\|off` | `off` | Automatic low-bitrate re-encodes for viewers who can't keep up (see below). |
| `adapt_ffmpeg path` | `ffmpeg` | ffmpeg executable for re-encodes. |
| `adapt_transcoders n` | `1` | Most re-encodes running at once. Each costs roughly a CPU core at 720p, less below. |
| `adapt_drop percent` | `5%` | Share of video frames dropped over the window that moves a viewer down. |
| `adapt_window time` | `10s` | How long a viewer is measured before a decision. |
| `adapt_headroom percent` | `70%` | Share of a viewer's measured throughput a re-encode may use. |
| `adapt_min_bitrate kbps` | `200k` | Floor for a re-encode's video bitrate. |
| `adapt_audio_bitrate kbps` | `64k` | Audio bitrate of re-encodes. |
| `adapt_preset name` | `veryfast` | x264 preset for re-encodes (`ultrafast` on a weak CPU). |
| `adapt_args …` | none | Extra ffmpeg output arguments for re-encodes. |
| `adapt_encoder x264\|vaapi` | `x264` | Re-encode on the CPU, or on an AMD/Intel GPU through VAAPI. |
| `adapt_vaapi_device path` | `/dev/dri/renderD128` | Which GPU (render node) VAAPI uses. |
| `adapt_budget kbps` | none | Cloud side of an uplink: home's upload. No re-encode starts that would push the source plus re-encodes past it. |
| `uplink_key value` | none | Home side: the key a cloud instance must present to `matinee_uplink`. |
| `uplink_pull ws[s]://… key` | disabled | Cloud side: take the stream (and re-encodes) from a home instance instead of RTMP ingest. |
| `matinee_uplink;` (location) | | Home side: the endpoint a cloud instance pulls from (usually behind `listen tunnel:…`). |

Times use nginx-style suffixes such as `250ms`, `5s`, and `1m`. `SIGHUP` atomically reloads the httpgrl and stream
settings; `SIGTERM`, `SIGINT`, and `SIGQUIT` perform a bounded graceful shutdown.

## Adaptive renditions

With `adapt on`, viewers whose link can't carry the stream get a re-encode sized for them, started on demand:

- Every viewer is measured continuously: the share of video the server had to drop for them, and the throughput
  their client acknowledges. Dropping `adapt_drop` or more over `adapt_window` (or, in sync mode, pausing the room)
  marks a viewer as struggling, and the throughput measured while struggling is what their link can take.
- Struggling viewers share a re-encode whose bitrate comes from the slowest of them: capacity × `adapt_headroom`,
  less the audio, never above 80% of the source. Eight viewers on fast links keep watching the source; the two on
  bad links get one stream made for the worse of the two. With `adapt_transcoders` above 1, viewers within a factor
  of two of each other share a re-encode.
- As people come, go and recover, the re-encode is retuned (by more than 30%, at most every 20 s): the new encoder
  starts, viewers move once it has a keyframe, then the old one stops.
- A viewer that watches its re-encode without a single drop for 30 s is tried on the source again (backing off,
  up to 5 minutes, if it still doesn't fit). A re-encode nobody is watching stops after 10 s.

Re-encodes are stamped on the same server clock as the source, so switching is seamless: at the next keyframe,
with no jump in time. The re-encode's own latency (an x264 `zerolatency` encode, typically 100-300 ms) comes out of
the viewer's delay budget. The stats overlay shows which stream a viewer is on.

## Two boxes: home and cloud

The setup this is built for: a small cloud box (1 CPU, 1 GB, huge bandwidth) serves the viewers, and a box at home
(OBS, real CPU, a GPU) does everything expensive. `conf/home.conf` and `conf/cloud.conf` are complete examples.

```
OBS --RTMP--> home matinee ==== reverse tunnel (home dials out, mTLS) ====> cloud matinee --WebSocket--> viewers
              GPU re-encodes <-------- "make me 450 kbit/s" -----------------  adaptation policy
```

- **Home dials out**, so nothing at home is reachable from the internet. httpgrl's reverse tunnel does it: the home
  box keeps TLS connections parked at the cloud, authenticated by a client certificate, and the cloud sends requests
  down them. Matinee on the cloud pulls from a location on its own httpgrl that `proxy_pass`es into the tunnel.
- **One connection carries everything**: the source exactly as OBS sent it, plus any re-encodes. Because every
  stream shares one clock mapping, re-encodes stay frame-aligned with the source and viewers switch seamlessly.
- **The cloud decides, home encodes.** The cloud is the only box that sees the viewers, so the adaptation policy
  runs there; when it wants a re-encode it asks home, which makes it on the GPU (`adapt_encoder vaapi`) and sends it
  up. `adapt_budget` keeps the source plus re-encodes within home's upload, and `adapt_transcoders` caps how many
  re-encodes run (1: the source plus one stream for whoever's struggling).
- If the link drops, the stream ends for viewers until home reconnects (httpgrl redials, backing off to 30 s).

Certificates for the tunnel (a private CA just for it; the cloud's public certificate can be any, e.g. Let's
Encrypt, as long as home trusts it):

```sh
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 3650 \
  -keyout tunnel-ca.key -out tunnel-ca.pem -subj "/CN=matinee tunnel CA"          # keep tunnel-ca.key offline
openssl req -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout home.key -out home.csr -subj "/CN=home"
printf 'subjectAltName=DNS:home\nextendedKeyUsage=clientAuth\n' > home.ext
openssl x509 -req -in home.csr -CA tunnel-ca.pem -CAkey tunnel-ca.key -CAcreateserial -days 825 \
  -extfile home.ext -out home.crt
```

`home.crt`/`home.key` go on the home box; `tunnel-ca.pem` goes on the cloud box (`ssl_client_certificate`).

## Proxy mode

To republish to a third party (a restream service, say). For home → cloud, use the uplink above instead: it carries
re-encodes on demand and keeps them aligned.

On the source host, add for example:

```nginx
matinee {
    ingest_listen 1935;
    ingest_key local-obs-key;
    proxy_pass rtmp://cloud.example:1935/live/cloud-stream-key;
    proxy_ffmpeg /usr/bin/ffmpeg;
    proxy_args -c:v libx264 -preset veryfast -tune zerolatency -bf 0
               -force_key_frames "expr:gte(t,n_forced*1)"
               -c:a aac -b:a 128k -ar 48000;
}
```

Matinee writes a bounded FLV stream to ffmpeg's standard input without invoking a shell. If ffmpeg exits or
falls more than 16 MiB behind, the publisher connection is reset rather than letting memory grow or forwarding a
corrupt predictive stream. The remote URL is redacted from ffmpeg error logs.

## Static client

`web/` is ordinary HTML, CSS, and JavaScript. It can be served by this process or any static host. For a separate
host, set `window.MATINEE_SERVER` in `web/config.js`, or open the page with
`?server=wss://stream.example/ws`. The WebSocket endpoint must be reachable from the browser and use TLS when the
page uses HTTPS.

The client needs WebCodecs and WebAudio (a current Firefox or Chromium-family browser). It continuously measures
server clock offset, reconnects forever with bounded backoff, and treats four seconds without any server traffic as
a dead connection.
