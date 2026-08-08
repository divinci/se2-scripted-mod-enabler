# SE2 Scripted Mod Enabler

C# script mods do not work in Space Engineers 2 out of the box. This turns them on.

Install it once, then press Play in Steam. You do not need launch arguments, config
edits, a launcher or an injector.

## Status: pre-release

The method works, and unit tests cover it. But it has only run on one machine, against
one game build. There is no point-and-click installer yet, so you install it from a
command line.

[docs/spike-log.md](docs/spike-log.md) lists every claim this repo makes. It says which
ones someone has watched happen, and which are still unproven.

## Why you need it

C# scripting ships inside the game, switched off. Two things stop you using it:

- turning it on needs a launch argument, `-loadScripts`
- even with that argument, the game crashes while it builds its own script whitelist

The crash comes before the game compiles any mod code. So a player who subscribes to a
script mod gets a mod that does nothing at all.

Neither problem belongs to the mod author. Neither is something a player should have to
know about. This fixes both.

## What it does

The enabler is a plugin, which is a small file the game loads when it starts. It does two
things:

- registers scripting, in place of the `-loadScripts` argument
- repairs the duplicate whitelist entry that crashes the world load

### What it changes on your disk

It adds one path to a list called `DEV_PLUGINS`. That list lives in the game's
`Game2/SpaceEngineers2.runtimeconfig.json` file, and the game already reads it at
startup. Nothing else in the game folder changes.

The plugin file itself sits in `%LOCALAPPDATA%\SE2ScriptedModEnabler\`. That is outside
the game folder, so Steam's *Verify integrity of game files* cannot delete it.

## Install

You need Space Engineers 2 and the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/divinci/se2-scripted-mod-enabler
cd se2-scripted-mod-enabler

export SE2_GAMEDIR=/mnt/s/steam/steamapps/common/SpaceEngineers2/Game2
./tools/build-plugin.sh
dotnet run --project src/smesetup -- install
```

Set `SE2_GAMEDIR` to your own `Game2` folder. The build reads the game's own files, so it
stops with an error if that path is wrong.

Then start the game from Steam as usual.

## Check it worked

```bash
dotnet run --project src/smesetup -- status
```

```
Working: Working (build 2.3.0.2798). Script mods will load.

  game           S:\steam\steamapps\common\SpaceEngineers2\Game2
  steam buildid  24225481
  install dir    C:\Users\you\AppData\Local\SE2ScriptedModEnabler
  plugin         C:\Users\you\AppData\Local\SE2ScriptedModEnabler\SE2ScriptedModEnabler.dll
  registered     yes
  DEV_PLUGINS:
    ..\Game2.ContentBuilder\Game2.ContentBuilder.csproj
    C:\Users\you\AppData\Local\SE2ScriptedModEnabler\SE2ScriptedModEnabler.dll

  last run: working at 2026-08-08T22:41:07Z
  game build     2.3.0.2798
  supported      2.3.0.2798
```

Read `status` first whenever something looks wrong. It reports one of these:

| Status | What it means |
|---|---|
| `Working` | Script mods will load. |
| `NeverRan` | Installed. Start the game once to confirm it works. |
| `Armed` | The plugin started, but the game never finished starting up. Launch it again. |
| `Paused` | The game updated to a build this release has not been tested on. Update the enabler. |
| `Degraded` | Partly working. Something the plugin expected was not there, so read the notes. |
| `Failed` | The plugin hit an error and stood down. The game still ran. |
| `NotInstalled` | Not installed. Script mods will not load. |
| `MissingDll` | Registered, but the plugin file is gone. Run `install` again. |
| `UnsafeDir` | Another file ending in `.dll` is in the plugin folder. Remove it before you start the game. |
| `OptedOut` | Switched off by `-noSme` or `SE2SME_DISABLE=1`. |
| `GameNotFound` | The tool could not find Space Engineers 2. Point at it with `--game-dir`. |

The `--json` output uses these same names in camelCase, such as `neverRan`.

## Switch it off without uninstalling

Add `-noSme` to the game's Steam launch options, or set `SE2SME_DISABLE=1`. The plugin
still loads, does nothing, and the game runs as though you had never installed it.

## Uninstall

```bash
dotnet run --project src/smesetup -- uninstall
```

This puts `runtimeconfig.json` back to exactly what Keen shipped. The line endings, the
indentation and the missing final newline all match, byte for byte. It then deletes the
install folder.

The tool cuts its own entry out of that file by position, rather than rewriting the file
as JSON. Rewriting the whole document would change parts of it we never touched.

Another tool may have added a `DEV_PLUGINS` entry of its own. Uninstall leaves that entry
alone and removes only ours. It never restores a backup over someone else's work.

## When the game updates

The enabler does nothing at all, on purpose.

The plugin carries a built-in list of the game builds it has been tested against. This
release lists one: `2.3.0.2798`. On any other build the plugin writes one line to its log
and stops. The game then runs exactly as it would without it, and `status` reports
`Paused`.

You cannot widen that list yourself. No environment variable, config file or flag will do
it. Widening it takes a new release, so someone has looked at the new build first.

The reason is blunt. The game calls the plugin with no error handling around it. If the
plugin throws an error, it does not break the mod. It breaks the game, for everyone who
installed this, with no clue why.

## Command reference

| Command | What it does |
|---|---|
| `status` | Reports what is installed, and whether the last run worked |
| `install` | Copies the plugin in and registers it |
| `uninstall` | Removes our entry and our files |
| `repair` | Installs again over the top, which is safe to repeat |

| Option | What it does |
|---|---|
| `--game-dir PATH` | The `Game2` folder, or the folder above it |
| `--install-dir PATH` | Where the plugin file lives, required off Windows |
| `--plugin PATH` | The built `SE2ScriptedModEnabler.dll` to copy in |
| `--dry-run` | Says what would happen, and writes nothing |
| `--json` | Prints the same information as JSON |

Exit codes are `0` for success, `1` for failure and `2` for bad usage. `status` returns
`1` unless it reports `Working`.

## Licence

MIT.
