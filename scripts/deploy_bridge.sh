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

# Record where node, the chat service, and a usable PATH live so the bridge
# can start the chat service itself when the game launches (the game's own
# environment has none of them — Steam launches with a bare PATH, and the
# service needs node plus the `claude` CLI). Without node this just warns:
# the plugin skips autostart and the service can be run by hand.
NODE_BIN="$(command -v node || true)"
if [ -n "$NODE_BIN" ]; then
  {
    echo "NODE=$NODE_BIN"
    echo "SCRIPT=$HERE/scripts/dof_chat_service.mjs"
    echo "PATH=$PATH"
  } > "$GAME/BepInEx/plugins/FMBridge/chat_service.env"
  echo "chat service autostart configured (chat_service.env)"
else
  echo "WARNING: node not found on PATH — chat service autostart disabled" >&2
fi

echo "Restart Football Manager 26 for the plugin to take effect."
