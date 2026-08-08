# Spike log

Whether the mechanism this repo is built on actually works, checked one claim at a time.

Everything here rests on a route found by reading the shipping binaries:
`PluginHost.LoadDevPlugins` reads `AppContext.GetData("DEV_PLUGINS")`, which is populated
from `configProperties` in `Game2/SpaceEngineers2.runtimeconfig.json` and split on `;`.
Appending one path to that file loads our plugin on the Steam **Play** button, with no
launch arguments at all. Reading a decompilation is not the same as watching it happen,
so this log is the watching.

Run each step with `./tools/spike-run.sh T<n>`. It arms the machine, prints exactly what
to click, waits, and then reads the evidence back. **Run them in order.** T0–T2 exist to
establish that the evidence can produce a negative at all; until T2 is loud, T1's silence
means nothing.

| Field | Value |
|---|---|
| Game build | `2.3.0.2798` |
| Steam buildid | `24225481` |
| Game dir | `S:\steam\steamapps\common\SpaceEngineers2\Game2` |
| Stock `runtimeconfig.json` | sha256 `959689aed61a7564d83a15f1fc7750bdba8762e5a10600008315ec18c2a9859c`, 745 bytes, CRLF, no BOM, no trailing newline |

Results are filled in as each is run. `—` means not yet run.

---

## Offline

These need no game and run in CI or WSL. They are here because a spike log that only
records the exciting parts is how a regression gets shipped.

| | Claim | Result |
|---|---|---|
| **O1** | `dotnet test` — 47 tests: the runtimeconfig splice is byte-exact across add/remove, survives BOM, CRLF, a missing trailing newline, non-ASCII earlier in the file, and refuses paths the game could not load; VDF/ACF parsing against captured fixtures; the build allowlist matches its metadata mirror. | **PASS** — 47/47 |
| **O2** | `PluginSurfaceTests` over the built DLL: references only `VRage.Core` and `VRage.Library`, names only the four ABI types, exactly one type implements a Keen interface, and every method whose IL touches a Keen type carries `NoInlining`. | **PASS** — and it earned its keep immediately: it caught `Diag.Fail`, the catch-handler callee of both entry points, missing `NoInlining`. |
| **O3** | `./tools/frame-proof.sh` — the frame rule, against a genuinely deleted assembly. | **PASS** — see T10 below |
| **O4** | Install → uninstall round trip against the real game folder restores sha256 `959689ae…` byte for byte, and removes the DLL, `install.json` and `backup/`. | **PASS** |

---

## T0 — baseline

Stock install, nothing of ours anywhere. Establishes the file every later comparison is
against, and that the game starts.

**Pass:** game reaches the menu; the plugin section of `probe.sh` says `(none)`;
runtimeconfig sha256 matches the baseline above.

**Result:** —

---

## T1 — the stock `DEV_PLUGINS` entry

Keen ships `..\Game2.ContentBuilder\Game2.ContentBuilder.csproj` in `DEV_PLUGINS`. That
file is not on a player's disk. Does `PluginHost` complain, and does it matter?

**Pass:** no `Plugin NOT loaded` line. On its own this proves nothing — see T2.

**Result:** —

---

## T2 — the control

The one that gives T1 meaning. Install normally, then delete the DLL, so `DEV_PLUGINS`
holds a well-formed Windows path with no file at the end of it. This is also the
installer's `MissingDll` state, so the test doubles as a check that the state is real.

**Pass:** a `Plugin NOT loaded` line **is** present, and the game still starts.

**If this run is also silent, stop.** It would mean the log cannot distinguish "loaded
fine" from "never loaded", and every result below it is unfalsifiable.

**Result:** —

---

## T3 — the whole point

Install, clear the launch options entirely, press Play.

**Pass:** `[SE2SME] armed for build 2.3.0.2798` in the log, and `last-run.json` reports
`state: working` with a timestamp from this run.

**Result:** —

---

## T4 — scripting registered without `-loadScripts`

Read-only; same launch as T3. `GameApp.AddScripting` is a private static taking the
`EngineBuilder`, and `OnBeforeEngineInstantiated` fires at `GameApp.cs:334` with that
same builder still open — so the flag should be replaceable by a reflective call.

**Pass:** `AddScripting invoked reflectively`, and **both** whitelist providers report a
reduced anchor count (`10 -> 9`). Both matter: `GameWhitelistProvider<T>`'s statics are
per closed generic type, so fixing `ModWhitelistProvider` leaves
`InGameWhitelistProvider` broken.

**Result:** —

---

## T5 — a script mod actually compiles and runs

The one that can kill the plan. Deploy `mods/ScriptProbe` from the parent repo, enable it
on a world, load the world.

**Pass:** `[ScriptProbe]` lines present, **and** the script-whitelist section is `(none)`.

A collision here would mean the anchor dedup is wrong. `docs/08-distribution-plan.md`
flagged a specific risk — that removing the duplicate `Game2.Client` anchor newly exposes
`IPhysics`/`VRage.Physics` to whitelist expansion. That was traced statically and found
harmless: `VRage.Physics`'s only non-`Keen` public type is
`System.Runtime.CompilerServices.AsyncVoidMethodBuilder`, which would have collided with
CoreLib, but it carries `[EditorBrowsable(Never)]` and is the sole public type in its
namespace, so `AllowAssembly` skips the namespace entirely. T5 is where that reasoning
gets tested against execution.

**Result:** —

---

## T6 — idempotency

A player who already had `-loadScripts` in their launch options. `AddScripting` does
`Dictionary.Add` on four fixed keys, so a second call throws `ArgumentException` from
inside the game's own startup.

**Pass:** `scripting already registered … not calling AddScripting again`; the anchor
dedup still ran; no `ArgumentException`.

**Result:** —

---

## T7 — the gate

`-smeFakeBuild:9.9.9.9999` tells the plugin it is on a build it has never seen. The
override is narrowing only: it is honoured only when it would make the gate *reject*, so
it can never arm the plugin on an untested build.

**Pass:** `state: paused`; no `AddScripting` line; no anchor lines; the world loads
exactly as it would with nothing installed.

**Result:** —

---

## T8 / T9 — fail closed

`PluginHost.Add` does not wrap `Activator.CreateInstance`, and
`InvokeOnBeforeEngineInstantiated` does not wrap the handler. A throw from either kills
game startup. `-smeSimulate:ctor-throw` and `-smeSimulate:handler-throw` simulate our own
bug at each stage.

**Pass:** the game reaches the menu (and, for T9, the world loads); `last-run.json`
records `state: failed` with the exception in `notes`.

**Result:** —

---

## T10 — the frame rule

Everything above rests on one claim about the runtime: *a try/catch cannot catch a
resolution failure caused by its own body, but a try/catch one frame up can.* If that is
false, the fail-closed design is decoration.

`tests/FrameProof` builds against a stub assembly which is then deleted, and runs four
probes that differ only in which frame the reference sits in.

**Offline half — PASS.** `./tools/frame-proof.sh`, .NET 10 runtime, Release:

| Probe | Outcome |
|---|---|
| 1 — callee `NoInlining`, try one frame up | **caught** by the try one frame up, `FileNotFoundException` — as designed |
| 2 — callee `AggressiveInlining`, try one frame up | caught one frame up too: the inliner declines a body it cannot resolve, so `NoInlining` is belt-and-braces against this particular failure. Kept regardless — it is not a documented guarantee, and the attribute costs nothing |
| 3 — reference in the try's own frame | **not caught**; escaped to the caller — the central claim, confirmed |
| 4 — reference on a branch never taken | **not caught**; escaped to the caller — resolution is per-method, so "we only call it if the version matches" is not a defence. This is why `BuildGate` names no game type at all rather than merely avoiding them on the reject path |

**In-game half:** load `FrameProof.dll` via `-plugins:` (never `DEV_PLUGINS` — a
deliberately broken assembly has no business in a file Steam owns).

**Pass:** the game reaches the menu, `frame-proof.json` reports `holds: true`.

**Result (in-game):** —

---

## T11 — uninstall is byte-exact

**Pass:** runtimeconfig sha256 back to the baseline exactly; DLL, `install.json` and
`backup/` gone.

Uninstall splices our segment out rather than restoring the backup, because Keen may have
legitimately changed the file in between. One asymmetry is deliberate and documented on
`RuntimeConfigPatcher.Remove`: if we *created* the `DEV_PLUGINS` key, removal empties it
but leaves it. As a pure function of the file, `Remove` cannot tell a key it added from
one Keen shipped empty — and to the game the two are identical, since `LoadPlugins`
splits with `RemoveEmptyEntries`.

**Result:** **PASS** offline against the real game folder (O4). Re-confirm after a real
game run.

---

## T12 — a foreign edit

Something else — another mod tool, or Keen — appends its own `DEV_PLUGINS` entry.

**Pass:** `status` lists both entries; after `uninstall` the foreign one is still there
and ours is not.

**Result:** —

---

## T13 — Steam *Verify integrity of game files*

Produces a fact either way. If the edit survives, installs persist across verifies. If it
does not, `status` has to detect the reversion and offer to redo it.

**Result:** —

---

## T14 — what a real update does

Cannot be forced. Record the current `buildid` and depot manifests now, re-check after the
next real update. The two questions: does the update rewrite `runtimeconfig.json` (losing
our entry), and does the gate correctly pause on the new build stamp?

**Result:** recorded — Steam buildid `24225481`, game build `2.3.0.2798`. Awaiting an
update.

---

## T15 — the cost of scripting when nothing uses it

The plugin arms unconditionally, so a player who installs it for one mod pays whatever
scripting costs on every world. If that number is large the product changes shape — it
would have to arm per-world instead.

**Pass:** a number, from the same world loaded under T0 and under T3.

**Result:** —

---

## Out of scope

Two questions this spike cannot answer, both from `docs/08-distribution-plan.md`:

- **S1** — does a `Scripts/` folder survive the mod.io publish → subscribe round trip? If
  it does not, this enabler is a materially different product. Worth running soon
  regardless of how the above goes.
- **S2** — can a player enable a subscribed script mod through the in-game UI?
