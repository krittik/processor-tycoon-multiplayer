#!/usr/bin/env bash
# Live two-instance test in testbeds A (host) and B (peer) (docs/TESTING.md). Requires both testbeds with the MP plugin
# deployed (build.ps1 -Deploy) and the fixtures installed. Starts the games if needed, loads FIXTURE on A, hosts, joins
# from B, runs the host clock at SPEED for SECONDS, then prints both status files and new log problems.
# Usage: tools/live-duo.sh [fixture] [seconds] [speed-index 1..3]
set -u
FIXTURE=${1:-mpfx-early-1975}
SECONDS_TO_RUN=${2:-30}
SPEED=${3:-2}
ROOT="$(cd "$(dirname "$0")/../.." && pwd)/artifacts/testbeds"
A="$ROOT/A"; B="$ROOT/B"
cli() { "$1/tools/pt-agent.exe" "${@:2}" 2>/dev/null; }
cmd() { mkdir -p "$1/mp-dev"; echo "$2" >> "$1/mp-dev/cmd.txt"; }
wait_status() { # dir, regex, timeout
  for _ in $(seq 1 "$3"); do grep -qE "$2" "$1/mp-dev/status.json" 2>/dev/null && return 0; sleep 1; done
  echo "timeout waiting for /$2/ in $1"; return 1
}
mkdir -p "$A/mp-dev" "$B/mp-dev"
rm -f "$A/mp-dev/status.json" "$B/mp-dev/status.json"
cli "$A" launch --timeout 60 >/dev/null & cli "$B" launch --timeout 60 >/dev/null; wait
sleep 3
a_lines=$(wc -l < "$A/BepInEx/LogOutput.log"); b_lines=$(wc -l < "$B/BepInEx/LogOutput.log")
cli "$A" game save-load "$FIXTURE" | head -c 160; echo
cmd "$A" "host 27960 Hosty"
wait_status "$A" '"session":"Running"' 20 || exit 1
cmd "$B" "join 127.0.0.1:27960 Peery"
wait_status "$B" '"session":"Running"' 60 || exit 1
cmd "$A" "speed $SPEED"
sleep "$SECONDS_TO_RUN"
cmd "$A" "speed 0"
sleep 2
for t in A B; do echo "== $t"; cat "$ROOT/$t/mp-dev/status.json"; echo; done
echo "== new log problems (A, B)"
tail -n +"$a_lines" "$A/BepInEx/LogOutput.log" | grep -E "Exception|Error|failed|refusing|unknown|differs" | cut -c1-300 | sort | uniq -c | sort -rn | head -15
tail -n +"$b_lines" "$B/BepInEx/LogOutput.log" | grep -E "Exception|Error|failed|refusing|unknown|mismatch" | cut -c1-300 | sort | uniq -c | sort -rn | head -15
