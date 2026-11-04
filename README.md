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

Times use nginx-style suffixes such as `250ms`, `5s`, and `1m`. `SIGHUP` atomically reloads the httpgrl and stream
settings; `SIGTERM`, `SIGINT`, and `SIGQUIT` perform a bounded graceful shutdown.

## Proxy mode

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
