using System;
using System.Runtime.CompilerServices;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Plugins;

namespace SE2ScriptedModEnabler;

/// <summary>
/// Makes C# script mods work on a stock Space Engineers 2 install, with no launch
/// arguments: registers scripting in place of <c>-loadScripts</c>, and repairs the
/// duplicate Game2.Client whitelist anchor that otherwise crashes the world load.
///
/// <para><b>Why this file looks the way it does.</b> PluginHost does not guard us.
/// <c>Add</c> calls <c>Activator.CreateInstance</c> with no try/catch around our
/// constructor's own throw, and <c>InvokeOnBeforeEngineInstantiated</c> invokes the
/// handler bare. Anything we throw propagates out of the GameApp constructor and takes
/// the process with it — so after a Keen update that renames a type we bind, every
/// player of every mod that depends on this would get a game that will not launch, with
/// no clue why. That, not a broken mod, is the failure that matters.</para>
///
/// <para>Hence two rules, both enforced by PluginSurfaceTests rather than by comment:</para>
/// <list type="number">
/// <item>Only four game types appear at compile time — IPlugin, PluginHost,
/// EngineBuilder and Log. They are the plugin ABI; if they move, PluginHost's own
/// unguarded <c>assembly.GetTypes()</c> has already thrown before we get a say.
/// Everything else goes through <see cref="GameBridge"/> by reflection, where a rename
/// is a logged line instead of a crash.</item>
/// <item>Every method that names a game type is <c>[MethodImpl(NoInlining)]</c> and is
/// called from inside a try/catch <em>one frame up</em>. The JIT resolves a method's
/// type references when it compiles that method, so a TypeLoadException fires on entry
/// to the method that mentions the missing type — a try/catch in that same method never
/// runs. Without NoInlining the compiler can collapse the two frames and quietly undo
/// this. T10 in docs/spike-log.md proves the mechanism against a deleted assembly.</item>
/// </list>
/// </summary>
public sealed class SE2ScriptedModEnablerPlugin : IPlugin
{
    /// <summary>
    /// Support hatch for verifying the fail-closed paths on a real machine
    /// (<c>ctor-throw</c> / <c>handler-throw</c>). Both simulate our own bug, not a
    /// missing type; the missing-type case needs a deleted assembly and lives in
    /// tests/FrameProof.
    ///
    /// <para>Also available as <c>-smeSimulate:ctor-throw</c>, because Steam offers
    /// launch options and not environment variables. Like every other hatch here it can
    /// only ever make the plugin do less.</para>
    /// </summary>
    private const string SimulateVar = "SE2SME_SIMULATE";

    private const string SimulateArg = "-smeSimulate:";

    public SE2ScriptedModEnablerPlugin(PluginHost host)
    {
        try
        {
            Arm(host);
        }
        catch (Exception ex)
        {
            Diag.Fail("arming", ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Arm(PluginHost host)
    {
        Simulate("ctor-throw");

        var gate = BuildGate.Decide();
        Diag.GameBuild = gate.Stamp;

        switch (gate.Verdict)
        {
            case GateVerdict.Armed:
                break;

            case GateVerdict.OptedOut:
                Diag.Say($"disabled by {gate.Reason}, doing nothing");
                Diag.Publish(Diag.StateOptedOut);
                return;

            default:
                // Fail closed. No UI, no retry, no widening: the game runs exactly
                // vanilla and the installer explains why the next time it is opened.
                Diag.Say($"paused — {gate.Reason}. The game will run unmodified; "
                       + "update SE2 Scripted Mod Enabler to re-enable script mods.");
                Diag.Publish(Diag.StatePaused);
                return;
        }

        Diag.Say($"armed for {gate.Reason}");

        // Deferred: with -loadScripts the stock AddScripting runs at GameApp.cs:322,
        // after PluginHost is built and just before this event fires at :334. Doing
        // the work now would mean deciding whether scripting is registered before the
        // game has had its chance to register it.
        host.OnBeforeEngineInstantiated += OnBeforeEngineInstantiated;
        Diag.Publish(Diag.StateArmed);
    }

    private static void OnBeforeEngineInstantiated(EngineBuilder engine)
    {
        try
        {
            Apply(engine);
        }
        catch (Exception ex)
        {
            Diag.Fail("applying", ex);
        }
    }

    /// <summary>
    /// Takes <c>object</c>, not EngineBuilder, so that the only method in the assembly
    /// naming EngineBuilder is the delegate target above.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Apply(object engine)
    {
        Diag.Flush();   // the constructor usually runs before Log.Default exists
        Simulate("handler-throw");

        var scripting = EnsureScripting(engine);

        var anchors = true;
        foreach (var provider in GameBridge.WhitelistProviders)
        {
            anchors &= GameBridge.TryDedupeAnchors(provider, out var detail);
            Diag.Say(detail);
        }

        Diag.Publish(scripting && anchors ? Diag.StateWorking : Diag.StateDegraded);
    }

    private static bool EnsureScripting(object engine)
    {
        var providers = GameBridge.CodeProviderCount(engine);

        if (providers is null)
        {
            // Not knowing is the one case that must not guess. AddScripting does
            // Dictionary.Add on four fixed keys; calling it a second time throws
            // ArgumentException from inside the game's own startup.
            Diag.Say("could not read ProjectManager CodeProviders — not registering scripting");
            return false;
        }

        if (providers > 0)
        {
            var why = BuildGate.LoadScriptsFlagPresent() ? "-loadScripts was passed" : "another plugin got there first";
            Diag.Say($"scripting already registered ({providers} code providers, {why}) — not calling AddScripting again");
            return true;
        }

        var ok = GameBridge.TryAddScripting(engine, out var detail);
        Diag.Say(detail);
        return ok;
    }

    private static void Simulate(string mode)
    {
        var requested = Environment.GetEnvironmentVariable(SimulateVar) ?? BuildGate.ArgValue(SimulateArg);
        if (requested == mode)
            throw new InvalidOperationException($"simulated {mode}");
    }
}
