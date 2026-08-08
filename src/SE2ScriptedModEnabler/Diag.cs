using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Keen.VRage.Library.Diagnostics;

namespace SE2ScriptedModEnabler;

/// <summary>
/// Everything the outside world learns about a run.
///
/// Two channels, because neither alone is enough. The game log carries the detail and
/// is what tools/probe-log.sh greps for, but it is only reachable once Log.Default
/// exists and a player will never find it. last-run.json is a small durable record in
/// %LOCALAPPDATA% that the installer reads to show "working" / "paused" / "failed"
/// without asking the player to open anything.
///
/// The plugin never shows UI. Its constructor runs before the platform window exists,
/// and a MessageBox from a pre-splash thread can hang the process — which is exactly
/// the failure this whole design is built to avoid. The installer does the talking.
/// </summary>
internal static class Diag
{
    internal const string Tag = "[SE2SME]";

    /// <summary>Gate passed, handler subscribed, engine not built yet.</summary>
    internal const string StateArmed = "armed";

    /// <summary>Scripting registered and both whitelists repaired. The good case.</summary>
    internal const string StateWorking = "working";

    /// <summary>Ran, but something we expected to find was not there. Details in notes.</summary>
    internal const string StateDegraded = "degraded";

    /// <summary>Build not on the allowlist. Deliberately inert; the game is untouched.</summary>
    internal const string StatePaused = "paused";

    /// <summary>We threw and caught ourselves. Details in notes.</summary>
    internal const string StateFailed = "failed";

    /// <summary>-noSme or SE2SME_DISABLE=1.</summary>
    internal const string StateOptedOut = "opted-out";

    private static readonly List<string> Notes = [];
    private static readonly List<string> Pending = [];

    internal static string? GameBuild;

    /// <summary>
    /// Record a line. Goes to the game log if that is up yet, and is buffered for a
    /// retry if it is not — the constructor typically runs before Log.Default exists.
    /// </summary>
    internal static void Say(string message)
    {
        Notes.Add(message);
        Pending.Add(message);
        Flush();
    }

    /// <summary>Retry any lines written before the game log was available.</summary>
    internal static void Flush()
    {
        if (Pending.Count == 0) return;

        try
        {
            // One frame up from the only method in this assembly that names a game
            // type. If Keen removes Log, GameLog fails to JIT and throws here rather
            // than out of the plugin and into the game's startup path.
            foreach (var line in Pending)
                if (!GameLog($"{Tag} {line}"))
                    return;   // log still not up; keep the buffer for the next attempt

            Pending.Clear();
        }
        catch (Exception)
        {
            // A game log we cannot reach is not worth failing over. last-run.json
            // still gets everything.
            Pending.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool GameLog(string line)
    {
        var log = Log.Default;
        if (log is null) return false;
        log.WriteLine(line);
        return true;
    }

    /// <summary>
    /// The catch body of both ABI entry points, and so subject to the same frame rule as
    /// the try body. Inlined into the constructor, this drags Say/Flush's type references
    /// into the frame that was supposed to be catching for them — and a catch handler
    /// cannot run at all if its own method failed to JIT.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Fail(string stage, Exception ex)
    {
        Say($"{stage} failed, staying inert: {ex.GetType().Name}: {ex.Message}");
        Publish(StateFailed);
    }

    /// <summary>
    /// Overwrite %LOCALAPPDATA%\SE2ScriptedModEnabler\last-run.json. Called on every
    /// exit path, including the failure ones, so the installer never has to guess
    /// whether a run happened.
    /// </summary>
    internal static void Publish(string state)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SE2ScriptedModEnabler");
            Directory.CreateDirectory(dir);

            var json = new StringBuilder();
            json.Append("{\n");
            Field(json, "state", state);
            Field(json, "pluginVersion", PluginVersion());
            Field(json, "gameBuild", GameBuild);
            Field(json, "supportedBuilds", KnownBuilds.Describe());
            Field(json, "timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            json.Append("  \"notes\": [");
            for (var i = 0; i < Notes.Count; i++)
            {
                json.Append(i == 0 ? "\n" : ",\n").Append("    ");
                Quote(json, Notes[i]);
            }
            json.Append(Notes.Count == 0 ? "]\n" : "\n  ]\n").Append('}');

            // Write-then-move so a crash mid-write cannot leave the installer reading
            // a truncated file.
            var final = Path.Combine(dir, "last-run.json");
            var temp = final + ".tmp";
            File.WriteAllText(temp, json.ToString(), new UTF8Encoding(false));
            File.Move(temp, final, overwrite: true);
        }
        catch (Exception)
        {
            // %LOCALAPPDATA% unwritable is a degraded install, not a reason to take
            // the game down with us.
        }
    }

    private static void Field(StringBuilder json, string name, string? value)
    {
        json.Append("  \"").Append(name).Append("\": ");
        if (value is null) json.Append("null"); else Quote(json, value);
        json.Append(",\n");
    }

    private static void Quote(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (var c in value)
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else json.Append(c);
                    break;
            }
        json.Append('"');
    }

    private static string PluginVersion() =>
        typeof(Diag).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Diag).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
