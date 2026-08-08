using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Keen.VRage.Core.Plugins;

namespace FrameProof;

/// <summary>
/// The in-game half of T10. <see cref="Probes"/> answers whether the frame rule holds;
/// this answers the other half — whether a plugin whose dependency has vanished still
/// lets the game reach the main menu. Only a real launch can settle that, because the
/// thing being tested is PluginHost's unguarded <c>Activator.CreateInstance</c>.
///
/// <para>Load it with a launch argument, never DEV_PLUGINS. A deliberately broken
/// assembly has no business being spliced into a file Steam owns:</para>
/// <code>-plugins:C:\Users\you\AppData\Local\SE2ScriptedModEnabler\frameproof\FrameProof.dll</code>
/// </summary>
public sealed class FrameProofPlugin : IPlugin
{
    private const string Tag = "[SE2SME-FrameProof]";

    public FrameProofPlugin(PluginHost host)
    {
        var findings = new List<string>();

        try
        {
            findings.AddRange(Probes.Run());
        }
        catch (Exception ex)
        {
            // Reaching here means a probe frame leaked all the way out, which is itself
            // the answer — and the game still starting is the other half.
            findings.Add($"outer: escaped every probe frame — {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            Publish(findings);
        }
        catch (Exception)
        {
            // Nothing left to report to.
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Publish(List<string> findings)
    {
        WriteJson(findings);

        foreach (var finding in findings)
        {
            // One frame up from the only method here that names a game type, per the
            // rule this file exists to test. Doing it the other way round — a try/catch
            // inside GameLog — is probe 3.
            try
            {
                GameLog($"{Tag} {finding}");
            }
            catch (Exception)
            {
                break;   // no game log; frame-proof.json already has everything
            }
        }
    }

    private static void WriteJson(List<string> findings)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SE2ScriptedModEnabler");
        Directory.CreateDirectory(dir);

        var json = new StringBuilder("{\n  \"timestampUtc\": \"")
            .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
            .Append("\",\n  \"holds\": ")
            .Append(Probes.Holds(findings) ? "true" : "false")
            .Append(",\n  \"findings\": [");

        for (var i = 0; i < findings.Count; i++)
            json.Append(i == 0 ? "\n" : ",\n").Append("    \"")
                .Append(findings[i].Replace("\\", "\\\\").Replace("\"", "\\\""))
                .Append('"');

        json.Append(findings.Count == 0 ? "]\n}" : "\n  ]\n}");

        File.WriteAllText(Path.Combine(dir, "frame-proof.json"), json.ToString(), new UTF8Encoding(false));
    }

    /// <summary>The only method here that names a game type. Its caller catches for it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GameLog(string line) =>
        Keen.VRage.Library.Diagnostics.Log.Default?.WriteLine(line);
}
