#nullable enable
// Pulsar compiles with nullable off, so every file turns it on explicitly.

using System;
using System.Collections.Generic;
using System.Linq;
using Keen.Game2;
using Keen.Game2.Simulation.Scripting;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.DCS.Builders;
using Keen.VRage.Library.Utils;

namespace SE2ScriptedModEnabler;

/// <summary>
/// The two edits, bound at compile time against the game's own assemblies.
///
/// <para>That binding is the version check. Pulsar rebuilds this from source on the
/// player's machine whenever the game's four-part file version changes
/// (<c>GitHubPlugin.CacheManifest.IsCacheValid</c>, plus a full cache wipe from
/// <c>Updater.GameUpdatePrompt</c>), compiling against the installed
/// <c>SpaceEngineers2.dll</c>, <c>VRage*.dll</c> and <c>Game2*.dll</c>
/// (<c>Modern/References.cs</c>). So if Keen renames or reshapes anything named below, the
/// build fails, Pulsar disables the plugin and says so, and the game starts unmodified —
/// all before a line of this runs. Reaching these members reflectively would hide them from
/// exactly that check.</para>
///
/// <para>What recompiling cannot see is meaning: a build where all of this still compiles
/// but no longer does what it did. That is what the preconditions in
/// <see cref="SE2ScriptedModEnablerPlugin"/> are for.</para>
///
/// <para>Verified against 2.3.0.2798:
/// <c>GameApp.AddScripting(EngineBuilder)</c> GameApp.cs:427, called from the
/// <c>-loadScripts</c> branch at :320;
/// <c>EngineBuilder.EntityBuilder</c> EngineBuilder.cs:21;
/// <c>EntityBuilder.Components</c> EntityBuilder.cs:138;
/// <c>ComponentBuildInfo.ObjectBuilder</c> EntityBuilder.cs:50;
/// <c>ProjectManagerEngineComponent.ObjectBuilder.CodeProviders</c>
/// ProjectManagerEngineComponent.cs:61;
/// <c>GameWhitelistProvider&lt;T&gt;.AllowedAssemblies</c> GameWhitelistProvider.cs:30.</para>
/// </summary>
internal static class GameBridge
{
    /// <summary>
    /// The ProjectManager object builder, found the way the game finds it itself
    /// (<c>GameApp.OB&lt;T&gt;</c>, GameApp.cs:482) — except that the game throws when it is
    /// missing and this returns null. The game is entitled to assume its own component
    /// list; a plugin looking at it mid-construction is not.
    /// </summary>
    internal static ProjectManagerEngineComponent.ObjectBuilder? FindProjectManagerBuilder(
        EngineBuilder engine)
    {
        List<EntityBuilder.ComponentBuildInfo>? components = engine.EntityBuilder.Components;
        if (components is null) return null;

        foreach (EntityBuilder.ComponentBuildInfo component in components)
            if (component.ObjectBuilder is ProjectManagerEngineComponent.ObjectBuilder builder)
                return builder;

        return null;
    }

    /// <summary>
    /// The body of the stock <c>-loadScripts</c> branch, called for the same effect.
    ///
    /// <para>Safe at this point: <c>OnBeforeEngineInstantiated</c> fires at GameApp.cs:334
    /// with the same builder the flag's branch would have used at :322, still inside its
    /// <c>using</c> block, and <c>CodeProviders</c> is not read until
    /// <c>EngineBuilder.Dispose</c> runs <c>ProjectManagerEngineComponent.Init</c>.</para>
    ///
    /// <para>Private on the game side; reachable because Pulsar publicizes
    /// <c>SpaceEngineers2</c> for this assembly — see AssemblyInfo.cs.</para>
    /// </summary>
    internal static void AddScripting(EngineBuilder engine) => GameApp.AddScripting(engine);

    /// <summary>
    /// Drop the duplicate whitelist anchors.
    ///
    /// <para><c>AddScripting</c> sets ten, two of them types in <c>Game2.Client</c>.
    /// <c>ConfigureWhitelist</c> expands each anchor to its whole assembly
    /// (GameWhitelistProvider.cs:31), so the second pass over that assembly throws
    /// <em>"The namespace Keen.Game2.Client is already allowed"</em> before a single mod is
    /// parsed. The first anchor already covers the assembly, so dropping the rest loses
    /// nothing — and because it only ever removes entries, it can only narrow the
    /// whitelist, never widen it.</para>
    /// </summary>
    internal static bool TryDedupeAnchors<TProvider>(out string detail)
        where TProvider : Singleton<TProvider>
    {
        var name = typeof(TProvider).Name;
        Type[] anchors = GameWhitelistProvider<TProvider>.AllowedAssemblies;

        if (anchors is null || anchors.Length == 0)
        {
            detail = $"{name}: no anchors set, scripting is off";
            return false;
        }

        Type[] firstPerAssembly = [.. anchors.DistinctBy(anchor => anchor.Assembly)];
        if (firstPerAssembly.Length == anchors.Length)
        {
            detail = $"{name}: {anchors.Length} anchors, no duplicates — fixed upstream?";
            return true;
        }

        var dropped = anchors.Except(firstPerAssembly).Select(anchor => anchor.FullName);
        GameWhitelistProvider<TProvider>.AllowedAssemblies = firstPerAssembly;

        detail = $"{name}: {anchors.Length} -> {firstPerAssembly.Length} anchors, "
               + $"dropped {string.Join(", ", dropped)}";
        return true;
    }
}
