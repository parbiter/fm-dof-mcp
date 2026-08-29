#!/bin/bash
# Copies the built FMBridge.dll into FM26's BepInEx plugins directory.
# Run scripts/build_bridge.sh first. Restart (or launch) the game afterwards
# for the plugin to load.
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${FM26_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Football Manager 26}"
DLL="$HERE/bridge/FMBridge/bin/Release/net6.0/FMBridge.dll"

[ -f "$DLL" ] || { echo "ERROR: $DLL not found — run scripts/build_bridge.sh first" >&2; exit 1; }
[ -d "$GAME/BepInEx" ] || { echo "ERROR: BepInEx not found under $GAME (set FM26_DIR if FM26 is installed elsewhere)" >&2; exit 1; }

mkdir -p "$GAME/BepInEx/plugins/FMBridge"
cp "$DLL" "$GAME/BepInEx/plugins/FMBridge/"
echo "deployed: $GAME/BepInEx/plugins/FMBridge/FMBridge.dll"
echo "Restart Football Manager 26 for the plugin to take effect."
