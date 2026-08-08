#!/usr/bin/env bash
# T10, offline half. Does a try/catch one frame up really catch a JIT-time resolution
# failure, and does NoInlining really make the difference?
#
#   ./tools/frame-proof.sh
#
# Builds FrameProofHost against FrameProofStub, deletes the stub from the output, and
# runs the probes. No game required — this is the part of T10 that is pure runtime
# semantics. tools/build-frame-proof.sh covers the other part: that a plugin in this
# state still lets the game reach the main menu.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$REPO/tests/FrameProof/FrameProofHost/bin/Release/net9.0"

dotnet build "$REPO/tests/FrameProof/FrameProofHost/FrameProofHost.csproj" -c Release -v q --nologo

[[ -f "$OUT/FrameProofStub.dll" ]] || {
    echo "the stub was not copied next to the host — the probes would be vacuous" >&2
    exit 1
}

rm -f "$OUT/FrameProofStub.dll"

echo
"$OUT/FrameProofHost"
