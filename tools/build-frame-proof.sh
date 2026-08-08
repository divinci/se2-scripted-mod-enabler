#!/usr/bin/env bash
# T10. Stage the frame-proof plugin next to a hole where its dependency used to be.
#
#   ./tools/build-frame-proof.sh
#
# Builds tests/FrameProof against FrameProofStub, copies the plugin into a staging
# folder, then deletes the stub from that folder. The plugin now has a hard reference to
# an assembly that does not exist, which is what a Keen rename looks like to the JIT.
#
# Loaded with -plugins:, never DEV_PLUGINS. A deliberately broken assembly has no
# business being spliced into a file Steam owns, and the frame semantics under test are
# the same either way.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAMEDIR="${SE2_GAMEDIR:-/mnt/s/steam/steamapps/common/SpaceEngineers2/Game2}"
LOCALAPPDATA="${SE2SME_LOCALAPPDATA:-/mnt/c/Users/$USER/AppData/Local}"
STAGE="$LOCALAPPDATA/SE2ScriptedModEnabler/frameproof"

[[ -d "$GAMEDIR" ]] || { echo "game not found: $GAMEDIR (set SE2_GAMEDIR)" >&2; exit 1; }
[[ -d "$LOCALAPPDATA" ]] || { echo "no such folder: $LOCALAPPDATA (set SE2SME_LOCALAPPDATA)" >&2; exit 1; }

BUILT="$REPO/tests/FrameProof/FrameProof/bin/Release/net9.0"

dotnet build "$REPO/tests/FrameProof/FrameProof/FrameProof.csproj" \
    -c Release -p:GAMEDIR="$GAMEDIR" -v q --nologo

[[ -f "$BUILT/FrameProofStub.dll" ]] || {
    echo "the stub was not copied next to the plugin — the test would be vacuous" >&2
    exit 1
}

rm -rf "$STAGE"
mkdir -p "$STAGE"
cp "$BUILT/FrameProof.dll" "$STAGE/"

# The whole experiment, in one line: the reference is compiled in, the file is not there.
[[ -f "$STAGE/FrameProofStub.dll" ]] && { echo "stub leaked into the staging folder" >&2; exit 1; }

WINPATH="$(python3 -c "
import sys
p = sys.argv[1].split('/')
print(p[2].upper() + ':\\\\' + '\\\\'.join(p[3:]))" "$STAGE/FrameProof.dll")"

rm -f "$LOCALAPPDATA/SE2ScriptedModEnabler/frame-proof.json"

cat <<EOF

staged: $STAGE/FrameProof.dll   (FrameProofStub.dll deliberately absent)

Set this as the Steam launch options for Space Engineers 2, then press Play:

  -plugins:$WINPATH

The game must reach the main menu. Read the result with:

  ./tools/probe.sh frame-proof
EOF
