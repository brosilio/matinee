#!/usr/bin/env bash
# Run a command behind a simulated bad network. No root needed.
#
#   scripts/badnet.sh [-p profile] [-c cubic|bbr] [-t host:port] [-l port] -- command [args...]
#   scripts/badnet.sh set <profile> [port] # change the profile of a running badnet (default port 8080)
#   scripts/badnet.sh profiles             # list profiles
#
# How: the command runs in its own (unprivileged) network namespace. Inside it, 127.0.0.1:<port> is a socat relay
# that reaches <host:port> on the real network through a Unix socket (filesystem sockets cross namespaces).
# netem shapes the namespace's loopback, so only the command's side of the connection is impaired; the server and
# the publisher run normally. It's real TCP: real loss, retransmits and congestion control.
#
# Delay and jitter apply in each direction (loopback carries both), so the round trip is twice the profile delay.
#
# -c picks the TCP congestion control for the namespace (bbr needs the tcp_bbr module: sudo modprobe tcp_bbr).
# The relay inside the namespace is the sender on the impaired hop, so this shows what that algorithm would do
# for a viewer if the server used it.
#
# Example (a second, impaired Firefox next to your normal one):
#   scripts/badnet.sh -p hotel -- firefox --no-remote --profile "$(mktemp -d)" http://127.0.0.1:8080
set -euo pipefail

STATE_BASE="${XDG_RUNTIME_DIR:-/tmp}/matinee-badnet"   # one subdirectory per instance, named by its port
TC=/usr/sbin/tc

profile_args() {
  case "$1" in
    clean)   echo "" ;;
    dsl)     echo "delay 15ms 5ms rate 20mbit" ;;
    mobile)  echo "delay 40ms 20ms distribution normal loss 1% rate 8mbit" ;;
    slow)    echo "delay 30ms 10ms distribution normal loss 0.5% rate 1mbit" ;;
    hotel)   echo "delay 80ms 40ms distribution normal loss 3% rate 3mbit" ;;
    awful)   echo "delay 150ms 60ms distribution normal loss 6% rate 1500kbit" ;;
    outage)  echo "loss 100%" ;;
    *) echo "unknown profile '$1' (try: $0 profiles)" >&2; return 1 ;;
  esac
}

apply_profile() {   # runs inside the namespace, as its root
  local args
  args=$(profile_args "$1")
  "$TC" qdisc del dev lo root 2>/dev/null || true
  if [[ -n "$args" ]]; then
    # Real links rarely reorder, but netem's jitter does unless its queue is a plain FIFO; and its default queue
    # (1000 packets) is seconds of buffering on a slow link. Use an in-order queue of ~100 packets.
    # shellcheck disable=SC2086
    "$TC" qdisc add dev lo root handle 1: netem $args limit 100
    "$TC" qdisc add dev lo parent 1:1 handle 10: pfifo limit 100
  fi
}

case "${1:-}" in
  profiles)
    for p in clean dsl mobile slow hotel awful outage; do printf '%-8s %s\n' "$p" "$(profile_args "$p")"; done
    exit 0 ;;
  set)
    [[ $# -eq 2 || $# -eq 3 ]] || { echo "usage: $0 set <profile> [port]" >&2; exit 2; }
    profile_args "$2" >/dev/null
    pid=$(cat "$STATE_BASE/${3:-8080}/pid" 2>/dev/null) || { echo "no badnet running on port ${3:-8080}" >&2; exit 1; }
    nsenter --target "$pid" --user --net --preserve-credentials "$0" __apply "$2"
    echo "badnet: profile $2"
    exit 0 ;;
  __apply) apply_profile "$2"; exit 0 ;;
  __inner)
    : "${OUTER_UID:?}" "${OUTER_GID:?}"
    shift
    profile=$1 port=$2 sock=$3 cc=$4; shift 4
    STATE_DIR="$STATE_BASE/$port"
    ip link set lo up
    if [[ -n "$cc" ]]; then
      echo "$cc" > /proc/sys/net/ipv4/tcp_congestion_control ||
        { echo "badnet: congestion control '$cc' unavailable (try: sudo modprobe tcp_$cc)" >&2; exit 1; }
    fi
    socat "TCP-LISTEN:$port,bind=127.0.0.1,fork,reuseaddr" "UNIX-CONNECT:$sock" &
    relay=$!
    # `set` enters the namespaces through this relay: it stays in the user namespace that owns the network one.
    echo $relay > "$STATE_DIR/pid"
    trap 'kill $relay 2>/dev/null' EXIT
    apply_profile "$profile"
    # Drop back to our own uid for the command (Firefox refuses to run as root). Not exec: the relay must be
    # stopped when the command ends, or it keeps the namespace (and our caller's pipes) alive.
    status=0
    unshare --user --map-user="$OUTER_UID" --map-group="$OUTER_GID" -- "$@" || status=$?
    exit $status ;;
esac

profile=mobile target=127.0.0.1:8080 port=8080 cc=""
while getopts "p:c:t:l:" o; do
  case "$o" in
    p) profile=$OPTARG; profile_args "$profile" >/dev/null ;;
    c) cc=$OPTARG ;;
    t) target=$OPTARG ;;
    l) port=$OPTARG ;;
    *) exit 2 ;;
  esac
done
shift $((OPTIND - 1))
[[ "${1:-}" == "--" ]] && shift
[[ $# -gt 0 ]] || { echo "usage: $0 [-p profile] [-t host:port] [-l port] -- command" >&2; exit 2; }

STATE_DIR="$STATE_BASE/$port"
mkdir -p "$STATE_DIR"
sock="$STATE_DIR/relay.sock"
rm -f "$sock"
socat "UNIX-LISTEN:$sock,fork,mode=600" "TCP:$target" &
relay=$!
trap 'kill $relay 2>/dev/null; rm -f "$sock" "$STATE_DIR/pid"' EXIT
for _ in $(seq 50); do [[ -S "$sock" ]] && break; sleep 0.02; done

echo "badnet: profile $profile${cc:+, $cc}, 127.0.0.1:$port -> $target" >&2
OUTER_UID=$(id -u) OUTER_GID=$(id -g) unshare --user --map-root-user --net --kill-child -- "$0" __inner "$profile" "$port" "$sock" "$cc" "$@"
