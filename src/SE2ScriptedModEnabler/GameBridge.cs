using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace SE2ScriptedModEnabler;

/// <summary>
/// Every reach into Game2.* and the deeper VRage internals, all of it late-bound.
///
/// Nothing in this file names a game type at compile time — every parameter is
/// <c>object</c> and every member is found by string. That is the whole point: a Keen
/// rename turns these methods into "returns false, logs a line" instead of a
/// TypeLoadException thrown out of the game's startup path, where nothing catches it.
///
/// Verified against 2.3.0.2798:
///   GameApp.AddScripting(EngineBuilder)                              GameApp.cs:427
///   EngineBuilder.EntityBuilder (public field)                  EngineBuilder.cs:21
///   EntityBuilder.Components (public field)                     EntityBuilder.cs:138
///   ComponentBuildInfo.ObjectBuilder (public field)              EntityBuilder.cs:50
///   ProjectManagerEngineComponent.ObjectBuilder.CodeProviders
///                                        ProjectManagerEngineComponent.cs:61
///   GameWhitelistProvider&lt;T&gt;.AllowedAssemblies (public static field)
///                                              GameWhitelistProvider.cs:30
/// </summary>
internal static class GameBridge
{
    private const string GameAsm = "SpaceEngineers2";
    private const string SimAsm = "Game2.Simulation";

    private const string ProjectManagerOb =
        "Keen.VRage.Core.EngineComponents.ProjectManagerEngineComponent+ObjectBuilder";
    private const string WhitelistProvider =
        "Keen.Game2.Simulation.Scripting.GameWhitelistProvider`1";

    internal static readonly string[] WhitelistProviders =
    [
        "Keen.Game2.Simulation.Scripting.ModWhitelistProvider",
        "Keen.Game2.Simulation.Scripting.InGameWhitelistProvider",
    ];

    /// <summary>
    /// How many code providers the ProjectManager object builder already has.
    ///
    /// This is the precondition for calling AddScripting: it does
    /// <c>CodeProviders.Add(ProjectType.Unknown, ...)</c> on a plain Dictionary, so a
    /// second call throws ArgumentException on the duplicate key and takes the process
    /// with it. Zero means nobody has registered scripting yet.
    ///
    /// Null means "could not tell", which callers must treat as "do not touch it".
    /// </summary>
    internal static int? CodeProviderCount(object engineBuilder)
    {
        var ob = FindObjectBuilder(engineBuilder, ProjectManagerOb);
        if (ob is null) return null;

        var providers = ob.GetType().GetProperty("CodeProviders", BindingFlags.Public | BindingFlags.Instance)
                          ?.GetValue(ob);

        return providers is ICollection collection ? collection.Count : null;
    }

    private static object? FindObjectBuilder(object engineBuilder, string obTypeName)
    {
        var entityBuilder = engineBuilder.GetType()
            .GetField("EntityBuilder", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(engineBuilder);
        if (entityBuilder is null) return null;

        // ComponentBuildInfo is a struct; iterating the List<T> as IEnumerable boxes
        // each element, which is fine for reading one field off it.
        var components = entityBuilder.GetType()
            .GetField("Components", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(entityBuilder) as IEnumerable;
        if (components is null) return null;

        foreach (var info in components)
        {
            if (info is null) continue;
            var ob = info.GetType().GetField("ObjectBuilder", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(info);
            if (ob is not null && ob.GetType().FullName == obTypeName) return ob;
        }

        return null;
    }

    /// <summary>
    /// Call the private static GameApp.AddScripting(EngineBuilder) — the body of the
    /// stock <c>-loadScripts</c> branch (GameApp.cs:320). Safe at
    /// OnBeforeEngineInstantiated (:334): it is the same builder instance, still inside
    /// its using block, and CodeProviders is not frozen until EngineBuilder.Dispose
    /// runs ProjectManagerEngineComponent.Init.
    /// </summary>
    internal static bool TryAddScripting(object engineBuilder, out string detail)
    {
        var gameApp = FindType(GameAsm, "Keen.Game2.GameApp");
        if (gameApp is null) { detail = "Keen.Game2.GameApp not found"; return false; }

        var method = gameApp.GetMethod("AddScripting", BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null) { detail = "GameApp.AddScripting not found"; return false; }

        var parameters = method.GetParameters();
        if (parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(engineBuilder))
        {
            detail = $"GameApp.AddScripting has an unexpected signature ({parameters.Length} params)";
            return false;
        }

        try
        {
            method.Invoke(null, [engineBuilder]);
        }
        catch (TargetInvocationException ex)
        {
            // Unwrap so the log says what actually went wrong inside AddScripting
            // rather than "Exception has been thrown by the target of an invocation".
            var inner = ex.InnerException ?? ex;
            detail = $"GameApp.AddScripting threw {inner.GetType().Name}: {inner.Message}";
            return false;
        }

        detail = "GameApp.AddScripting invoked reflectively";
        return true;
    }

    /// <summary>
    /// Drop the duplicate whitelist anchors.
    ///
    /// AddScripting sets ten anchors, two of them types in Game2.Client.
    /// ConfigureWhitelist expands each anchor to its whole assembly, so the second pass
    /// throws "The namespace Keen.Game2.Client is already allowed" before a single mod
    /// is parsed. The first anchor already covers the assembly, so dropping the extra
    /// loses nothing.
    /// </summary>
    internal static bool TryDedupeAnchors(string providerTypeName, out string detail)
    {
        var generic = FindType(SimAsm, WhitelistProvider);
        var provider = FindType(SimAsm, providerTypeName);
        if (generic is null || provider is null)
        {
            detail = $"{Short(providerTypeName)}: whitelist provider types not found";
            return false;
        }

        FieldInfo? field;
        try
        {
            field = generic.MakeGenericType(provider)
                           .GetField("AllowedAssemblies", BindingFlags.Public | BindingFlags.Static);
        }
        catch (ArgumentException ex)
        {
            // MakeGenericType validates the `where T : Singleton<T>` constraint.
            detail = $"{Short(providerTypeName)}: {ex.GetType().Name} closing the provider type";
            return false;
        }

        if (field is null || field.GetValue(null) is not Type[] anchors)
        {
            detail = $"{Short(providerTypeName)}: AllowedAssemblies not found";
            return false;
        }

        if (anchors.Length == 0)
        {
            detail = $"{Short(providerTypeName)}: no anchors set, scripting is off";
            return false;
        }

        // First anchor per assembly, order preserved.
        var deduped = anchors.DistinctBy(t => t.Assembly).ToArray();
        if (deduped.Length == anchors.Length)
        {
            detail = $"{Short(providerTypeName)}: {anchors.Length} anchors, no duplicates — fixed upstream?";
            return true;
        }

        var dropped = string.Join(", ", anchors.Except(deduped).Select(t => t.FullName));
        field.SetValue(null, deduped);
        detail = $"{Short(providerTypeName)}: {anchors.Length} -> {deduped.Length} anchors, dropped {dropped}";
        return true;
    }

    private static Type? FindType(string assemblySimpleName, string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != assemblySimpleName) continue;
            try
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type is not null) return type;
            }
            catch (Exception)
            {
                // A single unloadable assembly must not stop the search.
            }
        }
        return null;
    }

    private static string Short(string fullName) =>
        fullName[(fullName.LastIndexOf('.') + 1)..];
}
