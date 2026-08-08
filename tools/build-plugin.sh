#!/usr/bin/env bash
# Build the plugin against the installed game's assemblies.
#
#   ./tools/build-plugin.sh            # Release, into src/SE2ScriptedModEnabler/bin/Release/net9.0/
#   ./tools/build-plugin.sh Debug
#
# The output stays in the default bin path on purpose: PluginSurfaceTests looks for it
# there, and the installer copies from there. Nothing is written into the game folder.
#
# The two referenced assemblies (VRage.Core, VRage.Library) are the whole compile-time
# surface. Everything in Game2.* is reached by reflection, so this build does not break
# when Keen renames something -- which is the point.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAMEDIR="${SE2_GAMEDIR:-/mnt/s/steam/steamapps/common/SpaceEngineers2/Game2}"
CONFIG="${1:-Release}"

[[ -d "$GAMEDIR" ]] || { echo "game not found: $GAMEDIR (set SE2_GAMEDIR)" >&2; exit 1; }

dotnet build "$REPO/src/SE2ScriptedModEnabler/SE2ScriptedModEnabler.csproj" \
    -c "$CONFIG" -p:GAMEDIR="$GAMEDIR" -v q --nologo

OUT="$REPO/src/SE2ScriptedModEnabler/bin/$CONFIG/net9.0/SE2ScriptedModEnabler.dll"
[[ -f "$OUT" ]] || { echo "build reported success but produced no dll" >&2; exit 1; }

echo
echo "built: $OUT"
echo "install with: dotnet run --project src/smesetup -- install"
