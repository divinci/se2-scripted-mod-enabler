#!/usr/bin/env bash
# Read what the last run left behind.
#
#   ./tools/probe.sh                # state file + log evidence
#   ./tools/probe.sh state          # just last-run.json
#   ./tools/probe.sh frame-proof    # just frame-proof.json (T10)
#   ./tools/probe.sh log            # just the game log sections
#   ./tools/probe.sh -f             # follow the newest log live
#
# Two sources, because neither alone is trustworthy. last-run.json is written on every
# exit path including the failure ones, so its absence is itself evidence — it means the
# plugin was never constructed. The game log has the detail but is only reachable once
# Log.Default exists, which is after the constructor runs.
set -uo pipefail

LOGDIR="${SE2_LOGDIR:-/mnt/c/Users/$USER/AppData/Roaming/SpaceEngineers2/Temp/Logs}"
LOCALAPPDATA="${SE2SME_LOCALAPPDATA:-/mnt/c/Users/$USER/AppData/Local}"
STATEDIR="$LOCALAPPDATA/SE2ScriptedModEnabler"

section() { echo; echo "--- $1 ---"; }

show_json() {
    local file="$1" label="$2"
    if [[ ! -f "$file" ]]; then
        echo "(no $label — the plugin never got as far as writing one)"
        return 1
    fi
    echo "    $(stat -c '%y' "$file")"
    python3 -m json.tool "$file" 2>/dev/null || cat "$file"
}

# A run writes several files: the real log plus _Stats, _Mission, _Render12 and friends.
# The main log is the one ending in the bare pid, so require _<digits>.log.
mains() { ls -t "$LOGDIR"/SpaceEngineers2_*.log 2>/dev/null | grep -E '_[0-9]+\.log$'; }

newest_log() {
    local log
    log=$(mains | head -1)
    [[ -n "${log:-}" ]] || return 1

    # Launching again leaves a newer, emptier log on top. If the newest has no [SE2SME]
    # lines but a recent one does, that newer log is almost never the run being asked
    # about. Reported rather than done silently: with the gate paused, a genuinely silent
    # newest log is the correct answer and must not be skipped past.
    if ! grep -aq -E "\[SE2SME" "$log"; then
        local hit
        hit=$(mains | head -10 | while read -r f; do
            grep -aq -E "\[SE2SME" "$f" && { echo "$f"; break; }
        done)
        if [[ -n "${hit:-}" ]]; then
            echo "note: newest log ($(basename "$log")) has no [SE2SME] lines." >&2
            echo "      an older one does — check which run you meant: $(basename "$hit")" >&2
        fi
    fi
    echo "$log"
}

probe_state() {
    section "last run (last-run.json)"
    show_json "$STATEDIR/last-run.json" "last-run.json"
}

probe_frame() {
    section "frame proof (frame-proof.json)"
    show_json "$STATEDIR/frame-proof.json" "frame-proof.json"
}

probe_log() {
    local log
    log=$(newest_log) || { echo "No logs found in $LOGDIR" >&2; return 1; }

    section "log"
    echo "$log"
    echo "    $(stat -c '%y' "$log")"

    section "command line"
    # T6 and T7 both hinge on this: whether -loadScripts was passed, and whether the
    # opt-out argument was.
    grep -a "Environment.CommandLine" "$log" | tail -1 || echo "(not logged)"

    section "plugin"
    # PluginHost logs only on failure, so the [SE2SME] lines are the proof it loaded and
    # ran. "Plugin NOT loaded" is PluginHost's own line and means the path was wrong.
    grep -a -E "\[SE2SME|Plugin NOT loaded" "$log" || echo "(none — the plugin did not load, or loaded and said nothing)"

    section "script whitelist"
    # An empty section here is the T5 pass condition: scripting compiled without the
    # duplicate-anchor crash the dedup exists to prevent.
    grep -a -E "ScriptWhitelistException|MetadataException|already allowed|is ambiguous" "$log" \
        || echo "(none — no whitelist collision)"

    section "compiler diagnostics"
    # VRS1001 = whitelist rejection, CS#### = ordinary compile error. Either proves
    # Roslyn ran, i.e. scripting is actually live.
    grep -a -E "VRS1[0-9]{3}|: error CS|: warning CS" "$log" || echo "(none)"

    section "mod discovery"
    grep -a -E "Discovered local mod|Pushing Project" "$log" || echo "(none)"

    section "probe output"
    grep -a "\[ScriptProbe" "$log" || echo "(none — no script ran)"
}

case "${1:-all}" in
    -f)
        LOG=$(newest_log) || { echo "No logs found in $LOGDIR" >&2; exit 1; }
        echo "=== $LOG ==="
        tail -f "$LOG" | grep --line-buffered -E "SE2SME|ScriptProbe|VRS1[0-9]{3}|ScriptWhitelist|Pushing Project"
        ;;
    state)       probe_state ;;
    frame-proof) probe_frame ;;
    log)         probe_log ;;
    all)
        probe_state
        [[ -f "$STATEDIR/frame-proof.json" ]] && probe_frame
        probe_log
        echo
        ;;
    *)
        echo "usage: $0 [state|frame-proof|log|-f]" >&2
        exit 1
        ;;
esac
