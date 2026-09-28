#!/usr/bin/env bash
# Quits the testbed games (only processes started from the testbeds, via their own Agent CLI), runs the offline tests
# and deploys the MP plugin + spikes into every testbed. Usage: tools/deploy-testbeds.sh
set -eu
DEV="$(cd "$(dirname "$0")/../.." && pwd)"
# Folders only: agents sometimes leave files (screenshots) next to the testbeds.
TESTBEDS=$(cd "$DEV/artifacts/testbeds" && ls -d */ | tr -d /)
# Quit: the MP dev command first (works even with native dialogs open), the Agent CLI as fallback; then wait.
for t in $TESTBEDS; do [ -d "$DEV/artifacts/testbeds/$t/mp-dev" ] && echo quit >> "$DEV/artifacts/testbeds/$t/mp-dev/cmd.txt"; done
sleep 2
for t in $TESTBEDS; do "$DEV/artifacts/testbeds/$t/tools/pt-agent.exe" quit >/dev/null 2>&1 || true; done
for _ in $(seq 1 30); do
  running=$(powershell -NoProfile -Command "(Get-Process -Name \"Processor Tycoon Beta\" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like \"*artifacts*testbeds*\" }).Count")
  [ "$running" = "0" ] && break; sleep 1
done
rm -f "$DEV"/artifacts/testbeds/*/mp-dev/cmd.txt
dotnet run --project "$DEV/Multiplayer/tests/ProcessorTycoon.Mp.Tests" 2>&1 | tail -3
for t in $TESTBEDS; do
  dotnet build "$DEV/Multiplayer/Multiplayer.slnx" -c Debug -p:Deploy=true -p:DeployRoot="$DEV/artifacts/testbeds/$t" 2>&1 | grep -E " error |Build succeeded" | sort -u
done
# The Agent mod in the testbeds follows its local build when one exists (Agent/build.ps1 -NoDeploy): in this repository
# or in a clone of processor-tycoon-agent next to it.
AGENT=""; for a in "$DEV" "$DEV/../processor-tycoon-agent"; do [ -d "$a/Agent/src" ] && { AGENT="$(cd "$a" && pwd)"; break; }; done
AGENT_DLL=$([ -n "$AGENT" ] && ls "$AGENT"/Agent/src/ProcessorTycoon.Mod/bin/Debug/*/ProcessorTycoon.Mod.dll 2>/dev/null | head -1)
AGENT_CLI="$AGENT/artifacts/agent/tools/pt-agent.exe"
for t in $TESTBEDS; do
  [ -n "$AGENT_DLL" ] && [ -d "$DEV/artifacts/testbeds/$t/BepInEx/plugins/ProcessorTycoon.Mod" ] && cp "$AGENT_DLL" "$DEV/artifacts/testbeds/$t/BepInEx/plugins/ProcessorTycoon.Mod/"
  [ -f "$AGENT_CLI" ] && [ -d "$DEV/artifacts/testbeds/$t/tools" ] && cp "$AGENT_CLI" "$DEV/artifacts/testbeds/$t/tools/pt-agent.exe"
done
echo "Agent mod: ${AGENT_DLL:-not built} + CLI copied into: $TESTBEDS" | tr '\n' ' '; echo
