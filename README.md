# SE2 Scripted Mod Enabler

Some Space Engineers 2 mods need to run a little C# to do their job, and two things stop
them. The game only switches scripting on when you launch it with `-loadScripts`, and once
you do, a duplicate entry in the script whitelist aborts the world load with *"The
namespace Keen.Game2.Client is already allowed"*. This does both jobs at startup, so you
need neither the launch option nor a way round the crash.

It is a plugin for [Pulsar](https://github.com/SpaceGT/Pulsar), the plugin loader most
Space Engineers plugins use. Pulsar handles the installing, the updating and the on/off
switch. This repo is just the plugin.

Verified against Space Engineers 2 **2.3.0.2798** (VS2.3 – Drive, Automate & Detonate).
Later builds are Pulsar's business — see [When the game gets an
update](#when-the-game-gets-an-update).

## Do I need it?

- You subscribed to a mod that has scripts, switched it on, and nothing happened.
- A mod's page says it needs "scripts" or "scripting".
- A world will not load and the log says *"The namespace Keen.Game2.Client is already
  allowed"*. That is the whitelist bug, and you need the fix even if you already launch
  with `-loadScripts`.
- You make mods with scripts and you want other people to be able to use yours.

You do not need it for mods that only add blocks, paint, sounds or parts. Those already
work.

## Install it

1. Install [Pulsar](https://github.com/SpaceGT/Pulsar) and start Space Engineers 2 through
   it. Pulsar calls SE2 "Modern", to tell it apart from the first game.
2. Open the plugin list, find **Scripted Mod Enabler**, tick it.
3. Restart the game.

That is the lot. Pulsar downloads the source, builds it on your PC, and keeps it up to
date. There is nothing to download from this page, and nothing is written to the game's
own folder.

## Check it worked

Every launch it says what it did in Pulsar's log (`info.log`, next to Pulsar itself) and
in the game's log. Look for `[SE2SME]`:

```
[SE2SME] v0.4.0 loaded
[SE2SME] registered scripting via GameApp.AddScripting
[SE2SME] ModWhitelistProvider: 10 -> 9 anchors, dropped …Render.BlockRenderComponent
[SE2SME] InGameWhitelistProvider: 10 -> 9 anchors, dropped …Render.BlockRenderComponent
[SE2SME] script mods are enabled
```

Anything other than `script mods are enabled` on the last line means it did not finish, and
the lines above it say where it stopped. Nothing at all means the plugin was never loaded;
check Pulsar's own log. Quote that first `v…` line in a bug report — it is the only version
number the plugin has.

If you already launch with `-loadScripts`, you will see `scripting already registered`
instead of the second line. That is fine — the whitelist fix is the half you still need.

## Switch it off

Untick it in Pulsar's plugin list. Pulsar then does not even build it, and its safe-mode
option turns off every plugin at once.

## When the game gets an update

Space Engineers 2 is still being built, so an update can move the parts this plugin uses.

Pulsar rebuilds every plugin from source after a game update, against the game you actually
have. If this one no longer fits, it does not build, and Pulsar switches it off and says so.
So an update either leaves the plugin working or leaves you without it — it does not leave
you with a game that will not start. If that happens, the fix is a new version here, and
you will get it automatically.

## For developers

What it does is two things, at `PluginHost.OnBeforeEngineInstantiated`:

- calls the private static `Keen.Game2.GameApp.AddScripting(EngineBuilder)`, which is the
  body of the game's own `-loadScripts` branch, unless something already registered
  scripting — `CodeProviders.Add` on four fixed keys means a second call throws;
- drops the duplicate entries from `GameWhitelistProvider<T>.AllowedAssemblies`, which
  otherwise abort world load with *"The namespace Keen.Game2.Client is already allowed"*.
  It keeps the first anchor of each assembly and can only ever narrow the list.

Both are bound at compile time, and that binding is the version check: Pulsar recompiles
the plugin whenever the game's four-part file version changes, so a Keen rename fails the
build and the plugin is disabled before the game starts. Reaching these members reflectively
would hide them from exactly that check, which is why none of it is reflective.

`AddScripting` is private and `GameApp` is internal. Pulsar reaches them by scanning plugin
sources for an `IgnoresAccessChecksTo` attribute and rewriting that reference with
everything public before Roslyn sees it; `src/ClientPlugin/AssemblyInfo.cs` is that
attribute, and it is load-bearing. A local `GAMEDIR` build does the same job with
Krafs.Publicizer, which is the one thing here that needs a NuGet restore.

```bash
dotnet build SE2ScriptedModEnabler.slnx     # against the ABI stubs in tests/GameStubs
dotnet test tests/PluginSurface             # the suite that enforces the claims above

# and before a release, the same suite against a build made with the real assemblies —
# the only guard against the stubs drifting
dotnet build src/ClientPlugin -c Release -p:GAMEDIR=/path/to/Game2
SME_PLUGIN_DLL=src/ClientPlugin/bin/real/Release/net9.0/SE2ScriptedModEnabler.dll \
    dotnet test tests/PluginSurface -c Release
```

Releasing: bump `Version` in `Plugin.cs`, set `<Commit>` in `SE2ScriptedModEnabler.xml` to
the release SHA (full 40 characters — Pulsar fetches `archive/<Commit>.zip`), and open a PR
against [StarCpt/PluginHub-SE2](https://github.com/StarCpt/PluginHub-SE2). Never change
`<Id>`: Pulsar keys enabled state off it.

To run it out of a working copy, add this folder to Pulsar as a source (`-sources`), tick
the plugin, then open its details page and click **Load File** →
`SE2ScriptedModEnabler.xml`. The button is on the details page, not in the Sources dialog,
and it is disabled until the plugin is ticked; unticking throws the file away again.
Without it Pulsar compiles the whole repo, tests included.

## Licence

MIT, the same as [Pulsar](https://github.com/SpaceGT/Pulsar) — see [LICENSE](LICENSE).
