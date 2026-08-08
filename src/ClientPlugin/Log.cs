#nullable enable
// Pulsar compiles with nullable off, so every file turns it on explicitly.

using System;
using System.Collections.Generic;

// Aliased rather than imported: this type is also called Log, and a class in the current
// namespace silently wins over a using directive, so the bare name would quietly bind to
// ours instead of Keen's.
using GameLog = Keen.VRage.Library.Diagnostics.Log;

namespace SE2ScriptedModEnabler;

internal static class Log
{
    internal const string Tag = "[SE2SME]";

    /// <summary>
    /// Tagged lines the game log was not up to take yet. GameLog.Default is still null
    /// while the plugin's constructor runs, so in practice this is every line written
    /// before OnBeforeEngineInstantiated fires.
    /// </summary>
    private static readonly List<string> AwaitingGameLog = [];

    internal static void Info(string message)
    {
        var line = $"{Tag} {message}";

        // Under Pulsar this is the channel that always works: it needs no game at all, and
        // it is where a player is told to look.
        SE2ScriptedModEnablerPlugin.PulsarWrite(line);

        AwaitingGameLog.Add(line);
        Flush();
    }

    /// <summary>Retry any lines buffered before the game log was available.</summary>
    internal static void Flush()
    {
        if (AwaitingGameLog.Count == 0) return;

        var log = GameLog.Default;
        if (log is null) return;

        foreach (var line in AwaitingGameLog)
            log.WriteLine(line);

        AwaitingGameLog.Clear();
    }
}
