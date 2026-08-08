# SE2 Scripted Mod Enabler

Some Space Engineers 2 mods need to run a little code to do their job. The game will not
let them. This fixes that.

You set it up once. After that you just press Play, like always.

## Do I need it?

You need it if any of this sounds like you:

- you subscribed to a mod, switched it on, and nothing happened
- a mod's page says it needs "scripts" or "scripting"
- you make mods, and you want other people to be able to use yours

You do not need it for mods that only add blocks, paint, sounds or parts. Those already
work.

## Please read this bit

This is an early version, and we will not pretend otherwise.

- There is no proper installer yet. You set it up by pasting a few lines into a Windows
  tool called PowerShell. We give you the exact lines to paste.
- It has only ever been tried on one PC, with one version of the game.
- It is easy to undo. There is a "Remove it" section further down.

If that sounds like more bother than it is worth, that is fair enough. Come back when
there is a proper installer.

## What it puts on your PC

Two small things:

- one file, tucked away in your own AppData folder, where programs keep their bits and
  pieces
- one extra line in one of the game's own settings files

That is the lot. It does not change the game itself. It never goes online. It only does
anything during the few seconds while the game is starting up.

Remove it and that line goes back exactly as it was, down to the last character.

## Set it up

First you need two things:

1. Space Engineers 2, installed through Steam.
2. A free Microsoft download called the [.NET 9 SDK](https://dotnet.microsoft.com/download).
   It is the tool that builds the fix on your PC.

Now find your game folder. In Steam, right-click Space Engineers 2 and choose *Manage*,
then *Browse local files*. A window opens. Go into the folder called `Game2` and copy the
address from the bar along the top. It will look something like this:

```
C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2
```

Then do these four steps.

1. Get the files. Click the green *Code* button at the top of this page, choose *Download
   ZIP*, and unzip it somewhere you can find again.
2. Open PowerShell. Press the Windows key, type `powershell`, and press Enter.
3. Paste the lines below one at a time, pressing Enter after each. Swap both folders in
   quote marks for your own.
4. Start the game from Steam as normal.

```powershell
cd "C:\Users\you\Downloads\se2-scripted-mod-enabler-main"

dotnet build src\SE2ScriptedModEnabler\SE2ScriptedModEnabler.csproj -c Release -p:GAMEDIR="C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2"

dotnet run --project src\smesetup -- install
```

## Check it worked

Paste this into the same PowerShell window:

```powershell
dotnet run --project src\smesetup -- status
```

Look at the very first word it prints. If it says `Working`, you are done.

```
Working: Working (build 2.3.0.2798). Script mods will load.

  game           C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\Game2
  steam buildid  24225481
  install dir    C:\Users\you\AppData\Local\SE2ScriptedModEnabler
  plugin         C:\Users\you\AppData\Local\SE2ScriptedModEnabler\SE2ScriptedModEnabler.dll
  registered     yes

  last run: working at 2026-08-08T22:41:07Z
  game build     2.3.0.2798
  supported      2.3.0.2798
```

## If it says something else

Find your word in the left column.

| If it says | What to do |
|---|---|
| `Working` | Nothing. It is working. |
| `NeverRan` | Start the game once, then check again. |
| `Paused` | The game has had an update we have not caught up with yet. Wait for a new version of this. |
| `NotInstalled` | Setup did not finish. Run the `install` line again. |
| `MissingDll` | The fix's file has gone missing. Run the `install` line again. |
| `GameNotFound` | It cannot find your game. Add `--game-dir "your Game2 folder"` to the end of the line. |
| `UnsafeDir` | There are files in the fix's folder that should not be there. Delete them, then start the game. |
| `Armed` | The game did not finish starting up last time. Launch it again. |
| `Degraded` | It half worked. Read the notes it prints underneath. |
| `Failed` | It hit a snag and switched itself off. Your game was fine. Read the notes. |
| `OptedOut` | You switched it off yourself. See "Switch it off for a while" below. |

Steam has a *Verify integrity of game files* button that repairs the game. It cannot
delete the fix, because the fix does not live in the game's folder. It may undo that one
settings line, though. If script mods stop working after you use that button, run the
`install` line again.

## Switch it off for a while

You do not have to remove it. In Steam, right-click the game, choose *Properties*, and
type `-noSme` into the launch options box.

The fix then sits there doing nothing at all, and the game runs just as it did before.
Take `-noSme` back out to switch it on again.

## Remove it

```powershell
dotnet run --project src\smesetup -- uninstall
```

That puts the game's settings file back to exactly how it arrived, then deletes the
folder it made. Nothing of it is left behind.

If some other mod tool has added a line of its own to that file, this leaves that line
alone. It only ever removes its own.

## When the game gets an update

The fix switches itself off and the game runs normally. That is on purpose.

It only ever runs on versions of the game it has been tested on. Right now that is one
version, the one it calls `2.3.0.2798`. On anything else it stands aside and does
nothing, and `status` will say `Paused`.

Here is why it is so strict. The game gives the fix no safety net. If the fix goes wrong
at the wrong moment, the game will not start at all. That would hit everybody who
installed it, with nothing on screen to say why. Standing aside is always the safer
answer.

So after a game update, come back here and look for a newer version.

## The technical bits

You can skip this. It is here for people who want it.

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
| `--json` | Prints the same information as JSON, with the statuses in camelCase |

Exit codes are `0` for success, `1` for failure and `2` for bad usage. `status` returns
`1` unless it reports `Working`.

The fix is a VRage plugin. It registers scripting in place of the `-loadScripts` launch
argument, and repairs a duplicate whitelist entry that otherwise crashes the world load.
It hooks in through the `DEV_PLUGINS` list in the game's
`Game2/SpaceEngineers2.runtimeconfig.json`, which the game already reads at startup.
Uninstall splices that entry back out by byte offset, so the restore is exact.

[docs/spike-log.md](docs/spike-log.md) lists every claim this repo makes, and says which
ones someone has actually watched happen.

## Licence

MIT. Do what you like with it.
