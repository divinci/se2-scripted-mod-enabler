#!/usr/bin/env bash
# Drive one step of docs/spike-log.md: set the machine up, say exactly what to click,
# wait, then pull out the evidence.
#
#   ./tools/spike-run.sh T3
#   ./tools/spike-run.sh            # list the steps
#   ./tools/spike-run.sh T3 --setup-only
#
# The split is deliberate. Setup and evidence-reading are the parts that are easy to get
# subtly wrong and then believe — wrong log file, stale state file, forgot to uninstall
# first. Those are scripted. Launching the game and watching what happens is the part a
# person has to do, so the script stops and asks.
#
# Run them in order. T0-T2 establish that the evidence model can produce a negative at
# all; until T2 is loud, T1's silence proves nothing.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PARENT="${SE2_PARENT_REPO:-$(cd "$REPO/../.." 2>/dev/null && pwd)}"
GAMEDIR="${SE2_GAMEDIR:-/mnt/s/steam/steamapps/common/SpaceEngineers2/Game2}"
LOCALAPPDATA="${SE2SME_LOCALAPPDATA:-/mnt/c/Users/$USER/AppData/Local}"
STATEDIR="$LOCALAPPDATA/SE2ScriptedModEnabler"
RUNTIMECONFIG="$GAMEDIR/SpaceEngineers2.runtimeconfig.json"

# The stock file, captured before anything touched it. T11 compares against this.
BASELINE_SHA="959689aed61a7564d83a15f1fc7750bdba8762e5a10600008315ec18c2a9859c"

SETUP_ONLY=0
[[ "${2:-}" == "--setup-only" ]] && SETUP_ONLY=1

sme() { dotnet run --project "$REPO/src/smesetup" -c Release --no-build -- "$@"; }

rule() { printf '\n%s\n' "------------------------------------------------------------"; }
say()  { printf '%s\n' "$*"; }

sha() { [[ -f "$1" ]] && sha256sum "$1" | cut -d' ' -f1 || echo "(missing)"; }

# Evidence is only fresh if it postdates the launch. Stamping a marker before the run
# and comparing after is the difference between reading this run and reading last one's.
mark_time() { date +%s > /tmp/sme-spike-mark; }
newer_than_mark() {
    local f="$1" mark
    [[ -f "$f" ]] || return 1
    mark=$(cat /tmp/sme-spike-mark 2>/dev/null || echo 0)
    [[ "$(stat -c %Y "$f")" -ge "$mark" ]]
}

freshness() {
    local f="$STATEDIR/last-run.json"
    if [[ ! -f "$f" ]]; then
        say "!! no last-run.json at all — the plugin was never constructed"
    elif newer_than_mark "$f"; then
        say "   last-run.json is from this run"
    else
        say "!! last-run.json is STALE (older than this launch) — the plugin did not run"
    fi
}

launch() {
    local what="$1" options="${2:-}"
    rule
    say "Now, on the Windows side:"
    say
    if [[ -n "$options" ]]; then
        say "  1. Steam -> Space Engineers 2 -> Properties -> General -> Launch Options"
        say "     Set it to exactly:   $options"
        say "  2. Press Play."
    else
        say "  1. Steam -> Space Engineers 2 -> Properties -> General -> Launch Options"
        say "     Make sure it is EMPTY."
        say "  2. Press Play."
    fi
    say "  3. $what"
    say "  4. Quit the game."
    rule
    mark_time
    read -r -p "Press ENTER when the game has exited (or Ctrl-C to stop here): " _
}

require_build() {
    [[ -f "$REPO/src/SE2ScriptedModEnabler/bin/Release/net9.0/SE2ScriptedModEnabler.dll" ]] \
        || { say "plugin not built — run ./tools/build-plugin.sh first"; exit 1; }
    dotnet build "$REPO/src/smesetup" -c Release -v q --nologo >/dev/null || exit 1
}

case "${1:-}" in

T0) # ---------------------------------------------------------------------------------
    say "T0  baseline: stock install, nothing of ours anywhere."
    require_build
    sme uninstall
    rm -f "$STATEDIR/last-run.json" "$STATEDIR/frame-proof.json"
    say
    say "runtimeconfig sha256: $(sha "$RUNTIMECONFIG")"
    say "expected (baseline):  $BASELINE_SHA"
    [[ "$(sha "$RUNTIMECONFIG")" == "$BASELINE_SHA" ]] \
        && say "   -> matches; this is the file every later comparison is against" \
        || say "!! does NOT match. Either Keen shipped a new one (recapture the fixture) or something edited it."
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu, then load any world."
    "$REPO/tools/probe.sh" log
    say
    say "PASS if: game reaches the menu and the plugin section says (none)."
    ;;

T1) # ---------------------------------------------------------------------------------
    say "T1  the stock DEV_PLUGINS entry. Keen ships a path to a .csproj that is not"
    say "    on a player's disk; does PluginHost complain about it?"
    say
    say "current DEV_PLUGINS:"
    # Read stdout, not the exit code: status reports "not installed" as a failure, which
    # is the normal and expected state for T1. Captured rather than piped so pipefail
    # does not attribute smesetup's exit code to python's success.
    STATUS_JSON=$(sme status --json 2>/dev/null)
    if [[ -n "$STATUS_JSON" ]]; then
        printf '%s' "$STATUS_JSON" \
            | python3 -c "import json,sys; [print('   ', s) for s in json.load(sys.stdin)['devPlugins']]"
    else
        say "    (could not read the runtimeconfig — run ./tools/spike-run.sh T0 first)"
    fi
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu."
    "$REPO/tools/probe.sh" log
    say
    say "PASS if: no 'Plugin NOT loaded' line. Silence here means nothing on its own —"
    say "         T2 is what gives it meaning."
    ;;

T2) # ---------------------------------------------------------------------------------
    say "T2  the control. Point DEV_PLUGINS at a path with no file at the end of it and"
    say "    confirm the game says so. If this run is also silent, the log cannot tell us"
    say "    anything and every later test is worthless -- stop and fix the evidence model."
    require_build
    sme install
    rm -f "$STATEDIR/SE2ScriptedModEnabler.dll"
    rm -f "$STATEDIR/last-run.json"
    say
    say "installed, then deleted the DLL. DEV_PLUGINS now points at nothing:"
    say "   $(sha "$STATEDIR/SE2ScriptedModEnabler.dll")"
    sme status
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu."
    "$REPO/tools/probe.sh" log
    say
    say "PASS if: a 'Plugin NOT loaded' line IS present, and the game still started."
    say "         Loud here + silent in T1 is what makes T1 meaningful."
    ;;

T3) # ---------------------------------------------------------------------------------
    say "T3  the whole point: install, launch with no arguments at all."
    require_build
    sme uninstall >/dev/null
    "$REPO/tools/build-plugin.sh" >/dev/null || exit 1
    sme install
    rm -f "$STATEDIR/last-run.json"
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu, then load a world."
    freshness
    "$REPO/tools/probe.sh"
    say
    say "PASS if: '[SE2SME] armed for build ...' in the log and state is 'working'."
    ;;

T4) # ---------------------------------------------------------------------------------
    say "T4  read-only: the same launch as T3, looked at more closely. No relaunch."
    freshness
    "$REPO/tools/probe.sh"
    say
    say "PASS if: 'AddScripting invoked reflectively' appears, and BOTH whitelist"
    say "         providers report a reduced anchor count (10 -> 9)."
    ;;

T5) # ---------------------------------------------------------------------------------
    say "T5  the one that can kill the plan: does a script mod actually compile and run?"
    if [[ -d "$PARENT/mods/ScriptProbe" ]]; then
        python3 "$PARENT/tools/deploy-mod.py" ScriptProbe || exit 1
        say
        say "now set the world's mod list:"
        say "   python3 $PARENT/tools/set-modlist.py <world> ScriptProbe"
    else
        say "!! parent repo not found at $PARENT — deploy mods/ScriptProbe by hand"
        say "   (set SE2_PARENT_REPO if it lives elsewhere)"
    fi
    rm -f "$STATEDIR/last-run.json"
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Load the world that has ScriptProbe enabled. Let it run a few seconds."
    freshness
    "$REPO/tools/probe.sh"
    say
    say "PASS if: [ScriptProbe] lines are present AND the script whitelist section is"
    say "         (none). A whitelist collision here means the anchor dedup is wrong."
    ;;

T6) # ---------------------------------------------------------------------------------
    say "T6  idempotency: a player who already had -loadScripts in their launch options."
    say "    AddScripting does Dictionary.Add on fixed keys, so calling it twice throws"
    say "    from inside the game's own startup."
    rm -f "$STATEDIR/last-run.json"
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu, then load a world." "-loadScripts"
    freshness
    "$REPO/tools/probe.sh"
    say
    say "PASS if: 'scripting already registered ... not calling AddScripting again',"
    say "         the anchor dedup still ran, and there is no ArgumentException."
    ;;

T7) # ---------------------------------------------------------------------------------
    say "T7  the gate. Tell the plugin it is running on a build it has never seen and"
    say "    confirm it does nothing at all."
    rm -f "$STATEDIR/last-run.json"
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu, then load a world." "-smeFakeBuild:9.9.9.9999"
    freshness
    "$REPO/tools/probe.sh"
    say
    say "PASS if: state is 'paused', no AddScripting line, no anchor lines, and the world"
    say "         loads exactly as it would with nothing installed."
    ;;

T8) # ---------------------------------------------------------------------------------
    say "T8  fail-closed, constructor. PluginHost calls our ctor with no try/catch of its"
    say "    own, so this is the throw that would otherwise kill startup."
    rm -f "$STATEDIR/last-run.json"
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu." "-smeSimulate:ctor-throw"
    freshness
    "$REPO/tools/probe.sh" state
    say
    say "PASS if: the game reached the menu and state is 'failed'."
    ;;

T9) # ---------------------------------------------------------------------------------
    say "T9  fail-closed, engine handler. Same again one stage later, where the throw"
    say "    would come out of InvokeOnBeforeEngineInstantiated."
    rm -f "$STATEDIR/last-run.json"
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu, then load a world." "-smeSimulate:handler-throw"
    freshness
    "$REPO/tools/probe.sh" state
    say
    say "PASS if: the game reached the menu, the world loaded, and state is 'failed'."
    ;;

T10) # --------------------------------------------------------------------------------
    say "T10 the frame rule, against a genuinely missing assembly. Offline half first:"
    rule
    "$REPO/tools/frame-proof.sh" || { say; say "!! the frame rule did not hold offline — stop here"; exit 1; }
    rule
    say
    say "Now the in-game half: does a plugin in that state still let the game start?"
    "$REPO/tools/build-frame-proof.sh" || exit 1
    [[ $SETUP_ONLY == 1 ]] && exit 0
    launch "Reach the main menu." "-plugins:C:\\Users\\$USER\\AppData\\Local\\SE2ScriptedModEnabler\\frameproof\\FrameProof.dll"
    "$REPO/tools/probe.sh" frame-proof
    say
    say "PASS if: the game reached the menu, holds is true, and probe 3 did NOT catch"
    say "         in its own frame. Remember to clear the launch options afterwards."
    ;;

T11) # --------------------------------------------------------------------------------
    say "T11 uninstall leaves the game exactly as it found it."
    require_build
    say
    say "before: $(sha "$RUNTIMECONFIG")"
    sme uninstall
    say "after:  $(sha "$RUNTIMECONFIG")"
    say "wanted: $BASELINE_SHA"
    say
    if [[ "$(sha "$RUNTIMECONFIG")" == "$BASELINE_SHA" ]]; then
        say "PASS — byte-identical to the file Keen shipped."
    else
        say "FAIL — the file is not what it was. diff it against the fixture:"
        say "   diff <(xxd '$RUNTIMECONFIG') <(xxd '$REPO/tests/SE2ScriptedModEnabler.Setup.Tests/Fixtures/SpaceEngineers2.runtimeconfig.json')"
    fi
    say
    say "leftovers in $STATEDIR:"
    ls -la "$STATEDIR" 2>/dev/null || say "   (gone, as it should be)"
    ;;

T12) # --------------------------------------------------------------------------------
    say "T12 a foreign edit. Something else — another mod tool, or Keen — appends its own"
    say "    DEV_PLUGINS entry. We must notice and must not eat it."
    require_build
    sme install
    say
    say "now append a foreign entry by hand and re-run: ./tools/spike-run.sh T12"
    say "   (edit $RUNTIMECONFIG, add ';C:\\\\Other\\\\Thing.dll' inside DEV_PLUGINS)"
    say
    sme status
    say
    say "PASS if: status lists both entries, and after 'smesetup uninstall' the foreign"
    say "         one is still there and ours is not."
    ;;

T13) # --------------------------------------------------------------------------------
    say "T13 Steam 'Verify integrity of game files'. Produces a fact either way: either"
    say "    the edit survives, or it does not and 'status' has to detect that."
    say
    say "before: $(sha "$RUNTIMECONFIG")"
    sme status
    [[ $SETUP_ONLY == 1 ]] && exit 0
    rule
    say "Steam -> Space Engineers 2 -> Properties -> Installed Files -> Verify integrity."
    rule
    read -r -p "Press ENTER when the verify has finished: " _
    say
    say "after:  $(sha "$RUNTIMECONFIG")"
    sme status
    say
    say "Record which happened. Both outcomes are fine; what matters is that status is"
    say "right about it."
    ;;

T14) # --------------------------------------------------------------------------------
    say "T14 a real game update cannot be forced, so record the current state now and"
    say "    re-run this after the next one."
    say
    for f in "$GAMEDIR/../steamapps/appmanifest_1133870.acf" \
             "$(dirname "$GAMEDIR")/../../appmanifest_1133870.acf"; do
        [[ -f "$f" ]] && { grep -aE '"(buildid|StateFlags|LastUpdated)"' "$f"; break; }
    done
    say
    say "game build stamp:"
    sme status
    ;;

T15) # --------------------------------------------------------------------------------
    say "T15 what enabling scripting costs a player who has no script mods at all."
    say "    If it is expensive, the product changes shape: it would have to arm per-world"
    say "    rather than always."
    say
    say "Run T0 (nothing installed) and T3 (installed) back to back, loading the SAME"
    say "world, and compare. The numbers to pull out of each log:"
    say
    say "   grep -aE 'Loading world|World loaded|Session ready' <log>"
    say
    say "Then report both timings."
    ;;

*) # ---------------------------------------------------------------------------------
    say "usage: $0 <T0..T15> [--setup-only]"
    say
    say "  T0   baseline, nothing installed          T8   fail-closed: ctor throw"
    say "  T1   stock DEV_PLUGINS entry is quiet     T9   fail-closed: handler throw"
    say "  T2   a bad entry is loud  (do not skip)   T10  the frame rule vs a deleted asm"
    say "  T3   install + launch with no arguments   T11  uninstall is byte-exact"
    say "  T4   scripting registered (read-only)     T12  a foreign edit is preserved"
    say "  T5   a script mod compiles and runs       T13  Steam verify integrity"
    say "  T6   idempotent with -loadScripts         T14  record state for the next update"
    say "  T7   gate pauses on an unknown build      T15  cost of scripting when unused"
    say
    say "In order. T0-T2 first: until T2 is loud, T1's silence proves nothing."
    exit 1
    ;;
esac
