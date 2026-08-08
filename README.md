# SE2 Scripted Mod Enabler

Makes C# script mods work in **Space Engineers 2** on a normal Steam install — no launch
arguments, no editing config by hand, no launcher to remember. Install once, press Play.

> **Status: pre-release.** The mechanism works and is covered by tests, but it has only
> been run on one machine and one game build. The friendly point-and-click installer is
> not written yet; right now there is a command-line tool. See
> [docs/spike-log.md](docs/spike-log.md) for exactly what has and has not been proven.

## Why this exists

C# scripting is in the shipping game, but switched off. Turning it on needs a launch
argument (`-loadScripts`), and even with it the game **crashes while building its own
script whitelist** before it compiles anything. So a player who subscribes to a script mod
gets, at best, a mod that silently does nothing.

Neither problem is the mod author's to fix and neither is something a player should have
to know about. This closes both.

## What it does

1. Registers scripting at startup, in place of `-loadScripts`.
2. Repairs the duplicate whitelist anchor that otherwise crashes the world load.

It hooks in by appending one path to `DEV_PLUGINS` in
`Game2/SpaceEngineers2.runtimeconfig.json` — a list the game already reads on startup. No
launcher, no injector, no replaced game files. The plugin itself lives in
`%LOCALAPPDATA%\SE2ScriptedModEnabler\`, outside the game folder, so a Steam *Verify
integrity* cannot delete it.

## What it does when the game updates

**Nothing at all** — and that is the feature.

The plugin has a compiled-in list of the game builds it has been tested against. On any
other build it logs one line and returns, and the game runs exactly as if it were not
installed. There is no way to widen that list at runtime: not an environment variable, not
a config file, not a flag. Widening it takes a new release, which means someone has looked
at the new build first.

That matters more than it sounds. The game calls the plugin's constructor with no
`try`/`catch` around it, so anything thrown there does not break *the mod* — it breaks
*the game*, for everyone who installed this, with no clue why. A version gate that could
be talked into running on an untested build would be no gate at all.

## Install

Needs the [.NET 9 SDK](https://dotnet.microsoft.com/download) for now.

```bash
git clone https://github.com/divinci/se2-scripted-mod-enabler
cd se2-scripted-mod-enabler

./tools/build-plugin.sh              # needs Space Engineers 2 installed
dotnet run --project src/smesetup -- install
```

Then launch the game from Steam as usual.

```bash
dotnet run --project src/smesetup -- status
```

```
Working. Script mods are enabled.

  game           S:\steam\steamapps\common\SpaceEngineers2\Game2
  steam buildid  24225481
  plugin         C:\Users\you\AppData\Local\SE2ScriptedModEnabler\SE2ScriptedModEnabler.dll
  registered     yes
  last run       working — build 2.3.0.2798
```

`status` is the thing to read when something looks wrong; it reports one of:

| | |
|---|---|
| `working` | scripting registered, whitelist repaired |
| `paused` | the game updated to a build this release has not been tested on. Update the enabler |
| `neverRan` | installed, but the game has not been started since |
| `missingDll` | registered, but the plugin file is gone. Run `install` again |
| `degraded` | ran, but something expected was not found. Details in the notes |
| `failed` | the plugin threw and caught itself. The game was unaffected |

## Uninstall

```bash
dotnet run --project src/smesetup -- uninstall
```

`runtimeconfig.json` goes back to **byte-for-byte** what Keen shipped — same line endings,
same indentation, same absent trailing newline — and the install folder is removed. The
edit is a splice by byte offset rather than a JSON round trip, precisely so this is
possible; re-serialising the document would rewrite the whole file and make an exact
restore impossible.

If something else has added its own `DEV_PLUGINS` entry in the meantime, that entry is
left alone. Uninstall removes our segment; it does not restore a backup over the top of
someone else's work.

## For developers

```bash
./tools/build-plugin.sh                          # needs the game; sets $GAMEDIR from SE2_GAMEDIR
dotnet test tests/SE2ScriptedModEnabler.Setup.Tests
./tools/frame-proof.sh                           # T10, no game needed
./tools/spike-run.sh                             # list the in-game test steps
```

| | |
|---|---|
| `src/SE2ScriptedModEnabler/` | the plugin. One DLL, no dependencies |
| `src/SE2ScriptedModEnabler.Setup/` | installer engine. UI-free, no game references, unit-testable |
| `src/smesetup/` | command-line front end |
| `tests/FrameProof/` | T10's harness — deliberately built against an assembly that is then deleted |
| `docs/spike-log.md` | every claim, and whether it has actually been observed |

### The rule the plugin is built around

The JIT resolves a method's type references when it compiles that method. So a
`try`/`catch` **cannot** catch a `TypeLoadException` caused by its own body — the `try`
has to be one frame up, and the callee has to be `[MethodImpl(NoInlining)]` or the
compiler may collapse the two frames and quietly undo it. Resolution is also per-method,
not per-statement: a reference on a branch that never executes is resolved anyway.

That is not folklore; `./tools/frame-proof.sh` demonstrates all three against a genuinely
missing assembly in about a second.

Two consequences run through the code:

- Only four game types appear at compile time — `IPlugin`, `PluginHost`, `EngineBuilder`
  and `Log`. Everything else goes through `GameBridge` by reflection, where a Keen rename
  becomes a logged line instead of a crash.
- `BuildGate` names no game type at all, so the "is this build supported?" question stays
  answerable on a build where everything else has moved.

`PluginSurfaceTests` checks both against the compiled IL rather than trusting the comments
— including that every method touching a game type carries `NoInlining`. Without it the
discipline rots the first time someone adds a helper, and nothing about ordinary C# makes
moving a line between two methods look dangerous.

## Licence

MIT.
