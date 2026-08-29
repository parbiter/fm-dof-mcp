#!/bin/bash
# Builds the FMBridge BepInEx plugin (release configuration).
#
# Requires the .NET SDK and a Football Manager 26 install with BepInEx 6
# (IL2CPP) already installed, since the build references BepInEx's own
# core + interop assemblies (see docs/INSTALL.md for setup).
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
GAME="${FM26_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Football Manager 26}"
CORE="$GAME/BepInEx/core"
INTEROP="${FM26_INTEROP_DIR:-$GAME/BepInEx/interop}"

[ -d "$CORE" ] || { echo "ERROR: BepInEx core not found at $CORE (set FM26_DIR if FM26 is installed elsewhere)" >&2; exit 1; }

dotnet build "$HERE/bridge/FMBridge/FMBridge.csproj" -c Release \
  -p:CoreDir="$CORE" -p:InteropDir="$INTEROP"
echo "Built: $HERE/bridge/FMBridge/bin/Release/net6.0/FMBridge.dll"
