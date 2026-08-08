using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SE2ScriptedModEnabler;

internal enum GateVerdict
{
    /// <summary>Build is on the allowlist; go ahead.</summary>
    Armed,

    /// <summary>Build detected, but not one we have tested. Stay inert.</summary>
    UnknownBuild,

    /// <summary>Could not work out which build this is. Stay inert.</summary>
    StampUnreadable,

    /// <summary>The player asked us not to run.</summary>
    OptedOut,
}

internal readonly record struct GateDecision(GateVerdict Verdict, string? Stamp, string Reason);

/// <summary>
/// Decides whether this plugin is allowed to touch anything, before anything is touched.
///
/// Contains no game types at all — not even by reflection into game namespaces. The
/// decision is made from System.Reflection metadata and the command line, so it stays
/// answerable even on a build where every Keen type we care about has been renamed.
/// </summary>
internal static class BuildGate
{
    private const string GameAssembly = "SpaceEngineers2";

    /// <summary>Command-line opt-out. Also honoured as SE2SME_DISABLE=1.</summary>
    private const string OptOutArg = "-noSme";

    /// <summary>
    /// Test hatch for T7. Narrowing only: the override is honoured only when it would
    /// make the gate <em>reject</em>, so it can never be used to arm the plugin on a
    /// build that is not on the allowlist.
    /// </summary>
    private const string FakeStampVar = "SE2SME_FAKE_BUILD";

    /// <summary>
    /// The same hatch as a launch argument, because Steam offers launch options and not
    /// environment variables — a variable set outside Steam is not inherited until Steam
    /// itself restarts, which makes it useless for a scripted test run.
    /// </summary>
    private const string FakeStampArg = "-smeFakeBuild:";

    internal static GateDecision Decide()
    {
        if (OptedOut(out var how))
            return new GateDecision(GateVerdict.OptedOut, null, how);

        var stamp = DetectStamp(out var source);

        if (string.IsNullOrEmpty(stamp))
            return new GateDecision(GateVerdict.StampUnreadable, null,
                $"could not read the {GameAssembly} build stamp ({source})");

        var faked = Environment.GetEnvironmentVariable(FakeStampVar) ?? ArgValue(FakeStampArg);
        if (!string.IsNullOrEmpty(faked) && !KnownBuilds.Allows(faked))
        {
            stamp = faked;
            source = "build override";
        }

        return KnownBuilds.Allows(stamp)
            ? new GateDecision(GateVerdict.Armed, stamp, $"build {stamp} (from {source})")
            : new GateDecision(GateVerdict.UnknownBuild, stamp,
                $"build {stamp} (from {source}) is not one of: {KnownBuilds.Describe()}");
    }

    private static bool OptedOut(out string how)
    {
        if (Environment.GetEnvironmentVariable("SE2SME_DISABLE") == "1")
        {
            how = "SE2SME_DISABLE=1";
            return true;
        }

        foreach (var arg in SafeCommandLine())
        {
            if (!string.Equals(arg, OptOutArg, StringComparison.OrdinalIgnoreCase)) continue;
            how = OptOutArg;
            return true;
        }

        how = "";
        return false;
    }

    /// <summary>
    /// Four-part AssemblyFileVersion of the game assembly. VersionExt.Version is not
    /// usable for this — it packs Major*1e6 + Minor*1e3 + Build into an int and drops
    /// the revision, which is the only component that moves between builds.
    /// </summary>
    private static string? DetectStamp(out string source)
    {
        // By simple name over the loaded set, never GetEntryAssembly(): the entry
        // assembly is whatever host started the process, and under a launcher or a
        // test harness that is not the game.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != GameAssembly) continue;

            var attr = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
            if (!string.IsNullOrEmpty(attr?.Version))
            {
                source = "loaded assembly";
                return attr!.Version;
            }
        }

        // Fallback for the case where the plugin is loaded before the game assembly
        // is faulted in: read the file beside the running executable.
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var dll = Path.Combine(dir, GameAssembly + ".dll");
                if (File.Exists(dll))
                {
                    var info = FileVersionInfo.GetVersionInfo(dll);
                    if (!string.IsNullOrEmpty(info.FileVersion))
                    {
                        source = "file version beside the exe";
                        return info.FileVersion;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Deliberately swallowed — an unreadable stamp is a "stay inert" answer,
            // not an error to propagate into the game's startup path.
        }

        source = $"{GameAssembly} not loaded and no readable file beside the exe";
        return null;
    }

    /// <summary>True when the game was started with the stock scripting flag.</summary>
    internal static bool LoadScriptsFlagPresent()
    {
        foreach (var arg in SafeCommandLine())
            if (string.Equals(arg, "-loadScripts", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// The value of a <c>-name:value</c> launch argument, or null. Used only by the
    /// narrowing hatches — nothing here can widen what the gate allows.
    /// </summary>
    internal static string? ArgValue(string prefix)
    {
        foreach (var arg in SafeCommandLine())
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return arg[prefix.Length..];
        return null;
    }

    private static string[] SafeCommandLine()
    {
        try { return Environment.GetCommandLineArgs(); }
        catch (Exception) { return []; }
    }
}
