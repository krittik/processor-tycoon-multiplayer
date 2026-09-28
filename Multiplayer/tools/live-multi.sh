#!/usr/bin/env bash
# Live N-instance test: testbed A hosts FIXTURE, every other listed testbed joins, the host clock runs at SPEED for
# SECONDS; prints every status file and new log problems. Requires deployed testbeds (tools/deploy-testbeds.sh).
# Usage: tools/live-multi.sh [fixture] [seconds] [speed 1..3] [peer testbeds, default "B C D"]
set -u
FIXTURE=${1:-mpfx-early-1975}
SECONDS_TO_RUN=${2:-30}
SPEED=${3:-2}
PEERS=${4:-"B C D"}
ROOT="$(cd "$(dirname "$0")/../.." && pwd)/artifacts/testbeds"
cli() { "$ROOT/$1/tools/pt-agent.exe" "${@:2}" 2>/dev/null; }
cmd() { mkdir -p "$ROOT/$1/mp-dev"; echo "$2" >> "$ROOT/$1/mp-dev/cmd.txt"; }
wait_status() { for _ in $(seq 1 "$3"); do grep -qE "$2" "$ROOT/$1/mp-dev/status.json" 2>/dev/null && return 0; sleep 1; done; echo "timeout waiting for /$2/ in $1"; return 1; }
declare -A lines
for t in A $PEERS; do
  mkdir -p "$ROOT/$t/mp-dev"; rm -f "$ROOT/$t/mp-dev/status.json"
  cli "$t" launch --timeout 60 >/dev/null &
done
wait
sleep 3
for t in A $PEERS; do lines[$t]=$(wc -l < "$ROOT/$t/BepInEx/LogOutput.log"); done
cli A game save-load "$FIXTURE" | head -c 120; echo
cmd A "host 27960 Host-A"
wait_status A '"session":"Running"' 20 || exit 1
for t in $PEERS; do
  cmd "$t" "join 127.0.0.1:27960 Player-$t"
  wait_status "$t" '"session":"Running"' 90 || exit 1
done
cmd A "speed $SPEED"
sleep "$SECONDS_TO_RUN"
cmd A "speed 0"
sleep 3
for t in A $PEERS; do
  echo "== $t"; grep -oE '"(date|slot|resyncs|driftRepairs|lastCheckpointResult|captureMs|applyMs|players|error)":("[^"]*"|\[[^]]*\]|[0-9.]+)' "$ROOT/$t/mp-dev/status.json" | tr '\n' ' '; echo
  tail -n +"${lines[$t]}" "$ROOT/$t/BepInEx/LogOutput.log" | grep -E "Exception|Error|failed|refusing|unknown|differs|mismatch" | cut -c1-250 | sort | uniq -c | sort -rn | head -6
done
