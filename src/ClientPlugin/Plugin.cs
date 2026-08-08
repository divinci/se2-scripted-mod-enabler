#nullable enable
// Pulsar compiles with nullable off, so every file turns it on explicitly.

using System;
using Keen.Game2.Simulation.Scripting;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Plugins;
using Keen.VRage.Library.Utils;

namespace SE2ScriptedModEnabler;

/// <summary>
/// Registers C# mod scripting without <c>-loadScripts</c>, and drops the duplicate
/// whitelist anchor that otherwise aborts world load.
///
/// <para>Both edits are compile-time bound (see <see cref="GameBridge"/>), which puts the
/// "does this build still fit?" question where Pulsar already answers it: the plugin is
/// rebuilt from source against the installed game on every game update, and a build that no
/// longer fits does not load at all. What is left to check here is meaning rather than
/// shape, so each edit tests its own precondition first and declines rather than
/// guesses.</para>
/// </summary>
public sealed class SE2ScriptedModEnablerPlugin : IPlugin
{
    /// <summary>
    /// The only version a bug report will ever quote. A const because nothing else
    /// survives: Pulsar compiles these files with Roslyn directly, so there is no
    /// AssemblyInformationalVersion, GetName().Version is 0.0.0.0, and even the assembly
    /// name is derived from the repository rather than ours. Bump it on every release.
    /// </summary>
    private const string Version = "0.4.0";

#pragma warning disable CS0649 // assigned by Pulsar, by reflection, before our ctor runs
    private static object? PulsarLog;
#pragma warning restore CS0649

    private static bool pulsarLogBroken;

    /// <summary>
    /// Wrapped by <c>PluginInstance.Instantiate</c>, which catches, logs and disables the
    /// plugin — so anything that goes wrong in here fails the way Pulsar expects a plugin
    /// to fail, and needs no handling of its own.
    /// </summary>
    public SE2ScriptedModEnablerPlugin(PluginHost host)
    {
        Log.Info($"v{Version} loaded");

        // Deferred: with -loadScripts the stock AddScripting runs at GameApp.cs:322, after
        // PluginHost is built and just before this event fires at :334. Doing the work now
        // would mean deciding whether scripting is registered before the game has had its
        // chance to register it.
        host.OnBeforeEngineInstantiated += OnBeforeEngineInstantiated;
    }

    /// <summary>
    /// The one place that has to catch for itself. Keen raises this from
    /// <c>PluginHost.InvokeOnBeforeEngineInstantiated</c> with no try/catch, and Pulsar
    /// cannot add one — it only wraps the constructor, and its
    /// <c>FirstChanceException</c> handler looks for <c>MemberAccessException</c> alone.
    /// Anything that escapes here takes the game's startup with it.
    /// </summary>
    private static void OnBeforeEngineInstantiated(EngineBuilder engine)
    {
        try
        {
            Apply(engine);
        }
        catch (Exception ex)
        {
            Log.Info($"failed, leaving the game as it was: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Apply(EngineBuilder engine)
    {
        Log.Flush();   // the constructor usually runs before the game log exists

        var scripting = EnsureScripting(engine);

        // & rather than && so the second provider is always attempted: they are
        // independent, and half a whitelist fix is still worth logging accurately.
        var anchors = Dedupe<ModWhitelistProvider>() & Dedupe<InGameWhitelistProvider>();

        Log.Info(scripting && anchors
            ? "script mods are enabled"
            : "partly applied — script mods may not load");

        static bool Dedupe<TProvider>() where TProvider : Singleton<TProvider>
        {
            var ok = GameBridge.TryDedupeAnchors<TProvider>(out var detail);
            Log.Info(detail);
            return ok;
        }
    }

    /// <summary>
    /// <c>AddScripting</c> does <c>Dictionary.Add</c> on four fixed keys, so calling it a
    /// second time throws <c>ArgumentException</c> from inside the game's own startup. An
    /// empty <c>CodeProviders</c> is the tell that nobody has registered scripting yet;
    /// under Pulsar it can be non-empty either because <c>-loadScripts</c> was passed or
    /// because another plugin got there first.
    /// </summary>
    private static bool EnsureScripting(EngineBuilder engine)
    {
        var projectManager = GameBridge.FindProjectManagerBuilder(engine);
        if (projectManager is null)
        {
            // Not a shape we recognise, and the cost of guessing wrong is a game that does
            // not start. AddScripting itself throws in this case (GameApp.cs:492).
            Log.Info("no ProjectManager object builder on the engine — not registering scripting");
            return false;
        }

        var providers = projectManager.CodeProviders.Count;
        if (providers > 0)
        {
            var why = LoadScriptsFlagPresent()
                ? "-loadScripts was passed"
                : "another plugin got there first";
            Log.Info($"scripting already registered ({providers} code providers, {why}) "
                   + "— not calling AddScripting again");
            return true;
        }

        GameBridge.AddScripting(engine);
        Log.Info("registered scripting via GameApp.AddScripting");
        return true;
    }

    /// <summary>Decides the wording of one log line, nothing else.</summary>
    private static bool LoadScriptsFlagPresent()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
            if (string.Equals(arg, "-loadScripts", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// One line to Pulsar's log, when Pulsar injected a sink. Invoked as a plain delegate
    /// so this assembly never names NLog; the null second argument is the level, which
    /// Pulsar's <c>LogFile.WriteLine</c> defaults to Info.
    /// </summary>
    internal static void PulsarWrite(string line)
    {
        if (pulsarLogBroken) return;
        if (PulsarLog is not Delegate sink) return;

        try
        {
            sink.DynamicInvoke(line, null);
        }
        catch (Exception)
        {
            // A log we cannot reach is not worth failing over, nor worth retrying on every
            // subsequent line.
            pulsarLogBroken = true;
        }
    }
}
