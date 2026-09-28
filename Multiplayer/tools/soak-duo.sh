#!/usr/bin/env bash
# Unattended soak (ROADMAP M2 acceptance): A starts a NEW game and hosts, B joins, the host runs at SPEED for MINUTES;
# both status files are sampled every 30 s into artifacts/soak-<stamp>.log, followed by a summary.
# Usage: tools/soak-duo.sh [minutes] [speed 1..3]
set -u
MINUTES=${1:-6}
SPEED=${2:-3}
DEV="$(cd "$(dirname "$0")/../.." && pwd)"
ROOT="$DEV/artifacts/testbeds"
LOG="$DEV/artifacts/soak-$(date +%Y%m%d-%H%M%S).log"
cli() { "$ROOT/$1/tools/pt-agent.exe" "${@:2}" 2>/dev/null; }
cmd() { mkdir -p "$ROOT/$1/mp-dev"; echo "$2" >> "$ROOT/$1/mp-dev/cmd.txt"; }
field() { grep -oE "\"$2\":(\"[^\"]*\"|[0-9.]+|true|false)" "$ROOT/$1/mp-dev/status.json" 2>/dev/null | head -1 | cut -d: -f2- | tr -d '"'; }
wait_status() { for _ in $(seq 1 "$3"); do grep -qE "$2" "$ROOT/$1/mp-dev/status.json" 2>/dev/null && return 0; sleep 1; done; echo "timeout waiting for /$2/ in $1" | tee -a "$LOG"; return 1; }
for t in A B; do mkdir -p "$ROOT/$t/mp-dev"; rm -f "$ROOT/$t/mp-dev/status.json"; done
cli A launch --timeout 60 >/dev/null & cli B launch --timeout 60 >/dev/null; wait
sleep 4
a0=$(wc -l < "$ROOT/A/BepInEx/LogOutput.log"); b0=$(wc -l < "$ROOT/B/BepInEx/LogOutput.log")
echo "new game: $(cli A game session-new-start --company-name 'Soak Host' --founder-name Host | head -c 200)" | tee -a "$LOG"
wait_status A '"campaign":true' 120 || exit 1
sleep 3
cmd A "host 27960 Host"
wait_status A '"session":"Running"' 20 || exit 1
cmd B "join 127.0.0.1:27960 Peer"
wait_status B '"session":"Running"' 90 || exit 1
cmd A "speed $SPEED"
start=$(field A date)
for i in $(seq 1 $((MINUTES * 2))); do
  sleep 30
  echo "$(date +%H:%M:%S) A $(field A date) resyncs=$(field A resyncs) capture=$(field A captureMs) | B $(field B date) $(field B lastCheckpointResult) resyncs=$(field B resyncs) apply=$(field B applyMs) err='$(field B error)'" | tee -a "$LOG"
done
cmd A "speed 0"
sleep 3
{
  echo "== summary: $start -> $(field A date), host resyncs $(field A resyncs), peer resyncs $(field B resyncs), last peer checkpoint: $(field B lastCheckpointResult)"
  echo "== log problems A"; tail -n +"$a0" "$ROOT/A/BepInEx/LogOutput.log" | grep -E "Exception|Error|failed|differs" | cut -c1-250 | sort | uniq -c | sort -rn | head -10
  echo "== log problems B"; tail -n +"$b0" "$ROOT/B/BepInEx/LogOutput.log" | grep -E "Exception|Error|failed|mismatch" | cut -c1-250 | sort | uniq -c | sort -rn | head -10
} | tee -a "$LOG"
