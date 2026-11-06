#!/usr/bin/env bash
# A complete local test stack: a "home" and a "cloud" instance joined by httpgrl's reverse tunnel, HLS on, from
# the example configs in conf/. For trying the web player, VRChat (HLS) and OBS against the real topology on one
# machine (Linux, or WSL2 on Windows).
#
#   scripts/devstack.sh up [--publisher] [--encoder x264|vaapi]   build, generate certs and configs, start
#   scripts/devstack.sh down                                        stop everything this script started
#   scripts/devstack.sh status                                      what's running, and the URLs
#
#   --publisher   also stream an ffmpeg test pattern into home (otherwise point OBS at it)
#   --encoder     where home makes re-encodes: x264 (CPU, default) or vaapi (AMD/Intel GPU, native Linux)
#
# Ports: cloud web/HLS 18080 (all interfaces), tunnel 18443, home RTMP 1935, home LAN web 18081.
# State (certs, configs, logs, pids) lives in ${XDG_STATE_HOME:-~/.local/state}/matinee-devstack.
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
state="${XDG_STATE_HOME:-$HOME/.local/state}/matinee-devstack"
token="devstack-movie-night-0123"
cmd=${1:-status}; shift || true
publisher=0 encoder=x264
while [[ $# -gt 0 ]]; do
  case "$1" in
    --publisher) publisher=1 ;;
    --encoder) encoder=$2; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done

lan_ip() { ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -n1; }

stop() {
  for name in publisher home cloud; do
    local f="$state/$name.pid"
    [[ -f "$f" ]] || continue
    local pid; pid=$(cat "$f")
    if kill -0 "$pid" 2>/dev/null; then
      kill "$pid"
      for _ in $(seq 50); do kill -0 "$pid" 2>/dev/null || break; sleep 0.1; done
      echo "stopped $name ($pid)"
    fi
    rm -f "$f"
  done
}

running() { [[ -f "$state/$1.pid" ]] && kill -0 "$(cat "$state/$1.pid")" 2>/dev/null; }

status() {
  for name in cloud home publisher; do
    if running $name; then echo "$name: running (pid $(cat "$state/$name.pid"), log $state/$name.log)"; else echo "$name: stopped"; fi
  done
  local ip; ip=$(lan_ip)
  cat <<EOF

  web player     http://localhost:18080/          (password: popcorn)
  HLS / VRChat   http://localhost:18080/hls/$token/index.m3u8
  from the LAN   http://${ip:-<this-ip>}:18080/hls/$token/index.m3u8
  OBS            rtmp://${ip:-localhost}:1935/live   stream key: change-me
EOF
}

start() { # name, command...
  local name=$1; shift
  setsid bash -c 'echo $$ > "$0"; exec "$@"' "$state/$name.pid" "$@" > "$state/$name.log" 2>&1 < /dev/null &
}

case "$cmd" in
  down) stop; exit 0 ;;
  status) status; exit 0 ;;
  up) ;;
  *) echo "usage: $0 up [--publisher] [--encoder x264|vaapi] | down | status" >&2; exit 2 ;;
esac

stop
mkdir -p "$state"
command -v ffmpeg >/dev/null || echo "warning: ffmpeg not found: re-encodes (and --publisher) won't work" >&2

echo "building..."
dotnet build "$repo/src/Matinee" -c Release -o "$state/bin" -nologo -v quiet

# Throwaway tunnel certificates: a CA, the cloud's server certificate (localhost), home's client certificate (CN home).
if [[ ! -f "$state/home.crt" ]]; then
  ( cd "$state"
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 365 -keyout ca.key -out ca.pem -subj "/CN=matinee devstack CA" 2>/dev/null
    for n in cloud home; do
      cn=$([[ $n == cloud ]] && echo localhost || echo home)
      usage=$([[ $n == cloud ]] && echo serverAuth || echo clientAuth)
      openssl req -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout $n.key -out $n.csr -subj "/CN=$cn" 2>/dev/null
      printf 'subjectAltName=DNS:%s\nextendedKeyUsage=%s\n' "$cn" "$usage" > $n.ext
      openssl x509 -req -in $n.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 365 -extfile $n.ext -out $n.crt 2>/dev/null
    done )
fi

# Configs: the repo's examples, pointed at this machine, with HLS on.
sed -e "s|include mime.types;|include $repo/conf/mime.types;|" \
    -e "s|/srv/matinee/web|$repo/web|" \
    -e "s|/etc/matinee/cloud|$state/cloud|g" \
    -e "s|/etc/matinee/tunnel-ca.pem|$state/ca.pem|" \
    -e "s|server_name stream.example.com;|server_name localhost;|" \
    -e "s|listen 8443 ssl;|listen 18443 ssl;|" \
    -e "s|listen 8080 default_server;|listen 18080 default_server;|" \
    -e "s|listen \[::\]:8080 default_server;|listen [::]:18080 default_server;|" \
    -e "s|ws://127.0.0.1:8080/.home-uplink|ws://127.0.0.1:18080/.home-uplink|" \
    -e "s|# hls       on;|hls       on;|" \
    -e "s|# hls_token vrc-movie-night-change-me;|hls_token $token;|" \
    -e "s|# hls_start 3s;|hls_start 3s;|" \
    -e "s|# hls_low   600k;|hls_low   600k;|" \
    -e "s|# location /hls/ { matinee_hls; }|location /hls/ { matinee_hls; }|" \
    "$repo/conf/cloud.conf" > "$state/cloud.conf"
sed -e "s|include mime.types;|include $repo/conf/mime.types;|" \
    -e "s|/srv/matinee/web|$repo/web|" \
    -e "s|/etc/matinee/home|$state/home|g" \
    -e "s|connect stream.example.com:8443;|connect 127.0.0.1:18443; server_name localhost; trusted_certificate $state/ca.pem;|" \
    -e "s|listen 8080;|listen 18081;|" \
    -e "s|adapt_encoder      vaapi;|adapt_encoder      $encoder;|" \
    "$repo/conf/home.conf" > "$state/home.conf"

for c in cloud home; do
  dotnet "$state/bin/matinee.dll" -t -c "$state/$c.conf" > "$state/$c.check" 2>&1 || { cat "$state/$c.check" >&2; exit 1; }
done

start cloud dotnet "$state/bin/matinee.dll" -c "$state/cloud.conf"
start home dotnet "$state/bin/matinee.dll" -c "$state/home.conf"
echo "waiting for the tunnel..."
for _ in $(seq 60); do grep -q '"uplink: cloud connected"' "$state/home.log" 2>/dev/null && break; sleep 0.5; done
grep -q '"uplink: cloud connected"' "$state/home.log" || { echo "the tunnel didn't come up; see $state/home.log and $state/cloud.log" >&2; exit 1; }

if [[ $publisher == 1 ]]; then
  start publisher ffmpeg -hide_banner -loglevel error -re \
    -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=frequency=440 \
    -c:v libx264 -preset veryfast -tune zerolatency -b:v 2500k -maxrate 2500k -bufsize 1250k -g 30 -bf 0 \
    -c:a aac -b:a 128k -f flv rtmp://127.0.0.1:1935/live/change-me
fi
echo "up."
status
