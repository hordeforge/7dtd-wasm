#!/usr/bin/env bash
# Runs the 7dtd-wasm acceptance inside the local container: a fresh
# steamcmd install of the dedicated server with the modlet staged.
#
# Usage: ./run_acceptance.sh
# Prereqs: docker build -t 7dtd-wasm-acceptance . (see Dockerfile)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
DIST="$ROOT/dist"

mkdir -p "$HERE/logs"

docker rm -f 7dtd-wasm-acceptance >/dev/null 2>&1 || true

docker run -d --name 7dtd-wasm-acceptance \
  -p 8081:8081 \
  -p 26900:26900 \
  -e LD_LIBRARY_PATH=/game/Mods/1_HordeForge_WasmHost/Native \
  -v "$DIST/Mods/1_HordeForge_WasmHost:/game/Mods/1_HordeForge_WasmHost:ro" \
  -v "$DIST/Mods/Wasm:/game/Mods/Wasm:ro" \
  -v "$HERE/serverconfig.container.xml:/game/serverconfig.xml:ro" \
  -v "$HERE/logs:/logs" \
  7dtd-wasm-acceptance \
  /game/7DaysToDieServer.x86_64 \
    -logfile /logs/server.log \
    -quit -batchmode -nographics -dedicated \
    -configfile=/game/serverconfig.xml

echo "container started; log at $HERE/logs/server.log"
