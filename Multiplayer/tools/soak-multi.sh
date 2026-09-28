#!/usr/bin/env bash
# M3-style soak: A starts a NEW game and hosts, B C D join; the host runs at SPEED for MINUTES while every minute a
# random peer leaves and rejoins; peer B develops and releases a CPU through its Agent CLI. Samples go to
# artifacts/soak-multi-<stamp>.log, followed by a summary. Usage: tools/soak-multi.sh [minutes] [speed 1..3]
set -u
MINUTES=${1:-10}
SPEED=${2:-3}
PEERS="B C D"
DEV="$(cd "$(dirname "$0")/../.." && pwd)"
ROOT="$DEV/artifacts/testbeds"
LOG="$DEV/artifacts/soak-multi-$(date +%Y%m%d-%H%M%S).log"
cli() { "$ROOT/$1/tools/pt-agent.exe" "${@:2}" 2>/dev/null; }
cmd() { mkdir -p "$ROOT/$1/mp-dev"; echo "$2" >> "$ROOT/$1/mp-dev/cmd.txt"; }
field() { grep -oE "\"$2\":(\"[^\"]*\"|[0-9.]+|true|false)" "$ROOT/$1/mp-dev/status.json" 2>/dev/null | head -1 | cut -d: -f2- | tr -d '"'; }
wait_status() { for _ in $(seq 1 "$3"); do grep -qE "$2" "$ROOT/$1/mp-dev/status.json" 2>/dev/null && return 0; sleep 1; done; echo "timeout waiting for /$2/ in $1" | tee -a "$LOG"; return 1; }
say() { echo "$(date +%H:%M:%S) $*" | tee -a "$LOG"; }
declare -A lines
for t in A $PEERS; do mkdir -p "$ROOT/$t/mp-dev"; rm -f "$ROOT/$t/mp-dev/status.json"; cli "$t" launch --timeout 60 >/dev/null & done
wait
sleep 4
for t in A $PEERS; do lines[$t]=$(wc -l < "$ROOT/$t/BepInEx/LogOutput.log"); done
say "new game: $(cli A game session-new-start --company-name 'Soak Host' --founder-name Host | grep -oE '"outcome":"[^"]*"')"
wait_status A '"campaign":true' 120 || exit 1
sleep 3
cmd A "host 27960 Host"
wait_status A '"session":"Running"' 20 || exit 1
for t in $PEERS; do cmd "$t" "join 127.0.0.1:27960 P$t"; wait_status "$t" '"session":"Running"' 90 || exit 1; done
cli B game cpu-preview --name "Soak One" --frequency-mhz 0.5 >/dev/null
review=$(cli B game cpu-review "Soak One" --target-market Industries --planned-price 60)
rid=$(echo "$review" | grep -oE '"reviewId":"[^"]+"' | head -1 | cut -d'"' -f4)
acks=$(echo "$review" | grep -oE '"requiredAcknowledgements":\[[^]]*\]' | head -1 | grep -oE '"[a-z_]+"' | tr -d '"' | grep -v requiredAcknowledgements | paste -sd, -)
say "B develops: $(cli B game cpu-develop "Soak One" --review-id "$rid" ${acks:+--acknowledge-risks $acks} --accept-missing-evidence true --decision-reason "MP soak test" | grep -oE '"(outcome|code)":"[^"]*"' | head -1)"
cli B game dialog-read | grep -q CpuConfirmationWindow && cli B game dialog-choose Confirm --dialog CpuConfirmationWindow >/dev/null
cmd A "speed $SPEED"
released=no
for i in $(seq 1 "$MINUTES"); do
  sleep 50
  if [ "$released" = no ] && cli B game dialog-read | grep -q 'Project Completed'; then
    say "B releases: $(cli B game projects-release 'Soak One' --price 60 --sell-on-market true | grep -oE '"released":[a-z]*')"; released=yes
  fi
  peers=($PEERS); victim=${peers[$(( (i - 1) % ${#peers[@]} ))]}
  cmd "$victim" leave; sleep 5; cmd "$victim" "join 127.0.0.1:27960 P$victim"
  wait_status "$victim" '"session":"Running"' 60
  say "A $(field A date) resyncs=$(field A resyncs) | $(for t in $PEERS; do echo -n "$t $(field $t date) $(field $t lastCheckpointResult) r=$(field $t resyncs); "; done) rejoined=$victim"
done
cmd A "speed 0"
sleep 3
{
  echo "== summary: A $(field A date), host resyncs $(field A resyncs), B released CPU: $released"
  echo "== host sees B's CPU: $(cli A game market-catalog --view market | grep -o 'PB | Soak One[^"]\{0,40\}' | head -1)"
  for t in A $PEERS; do echo "== sales drift measured on $t (D24): $(field $t salesDriftPercent)%"; done
  for t in A $PEERS; do
    echo "== log problems $t"; tail -n +"${lines[$t]}" "$ROOT/$t/BepInEx/LogOutput.log" | grep -E "Exception|Error|failed|differs|mismatch" | cut -c1-250 | sort | uniq -c | sort -rn | head -8
  done
} | tee -a "$LOG"
