using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SE2ScriptedModEnabler.Setup;

public enum InstallStatus
{
    /// <summary>Space Engineers 2 was not found. Nothing can be said about the rest.</summary>
    GameNotFound,

    /// <summary>No entry of ours in DEV_PLUGINS. The game is stock.</summary>
    NotInstalled,

    /// <summary>Registered, but the DLL it points at is gone — most likely a botched update.</summary>
    MissingDll,

    /// <summary>Another DLL is sitting in our folder, which the game would put on its resolve path.</summary>
    UnsafeDir,

    /// <summary>Installed, but the game has not been started since.</summary>
    NeverRan,

    /// <summary>Installed and the game started, but the engine was never built. Usually a crash elsewhere.</summary>
    Armed,

    /// <summary>Working. Script mods will load.</summary>
    Working,

    /// <summary>Ran, but something expected was missing. See the notes.</summary>
    Degraded,

    /// <summary>SE2 was updated to a build we have not verified, so the plugin stood down.</summary>
    Paused,

    /// <summary>The plugin caught itself throwing. See the notes.</summary>
    Failed,

    /// <summary>Switched off with -noSme or SE2SME_DISABLE=1.</summary>
    OptedOut,
}

/// <summary>What the plugin wrote to last-run.json the last time the game started.</summary>
public sealed record LastRun
{
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("pluginVersion")] public string? PluginVersion { get; init; }
    [JsonPropertyName("gameBuild")] public string? GameBuild { get; init; }
    [JsonPropertyName("supportedBuilds")] public string? SupportedBuilds { get; init; }
    [JsonPropertyName("timestampUtc")] public string? TimestampUtc { get; init; }
    [JsonPropertyName("notes")] public string[] Notes { get; init; } = [];
}

/// <summary>
/// What the installer itself did, so a later run can tell its own edit apart from
/// somebody else's. <see cref="PatchedSha256"/> is the hash of runtimeconfig.json as we
/// left it; if the file no longer hashes to that but our entry is still in it, the file
/// has been changed by Keen or by hand since — worth saying out loud, and a reason not
/// to restore the backup over the top of it.
/// </summary>
public sealed record InstallRecord
{
    [JsonPropertyName("gameDir")] public string GameDir { get; init; } = "";
    [JsonPropertyName("pluginPath")] public string PluginPath { get; init; } = "";
    [JsonPropertyName("pluginPathWindows")] public string PluginPathWindows { get; init; } = "";
    [JsonPropertyName("patchedSha256")] public string PatchedSha256 { get; init; } = "";
    [JsonPropertyName("originalSha256")] public string OriginalSha256 { get; init; } = "";
    [JsonPropertyName("installedUtc")] public string InstalledUtc { get; init; } = "";
    [JsonPropertyName("toolVersion")] public string ToolVersion { get; init; } = "";
}

public sealed record InstallReport(
    InstallStatus Status,
    string Headline,
    GameInstall? Game,
    string? InstallDir,
    string? PluginPathWindows,
    bool Registered,
    bool PluginPresent,
    bool ForeignEdit,
    IReadOnlyList<string> DevPlugins,
    LastRun? LastRun,
    InstallRecord? Record,
    IReadOnlyList<string> Notes);

public static class Files
{
    public const string PluginDll = "SE2ScriptedModEnabler.dll";
    public const string LastRun = "last-run.json";
    public const string InstallRecord = "install.json";
    public const string BackupDir = "backup";
    public const string RuntimeConfig = "SpaceEngineers2.runtimeconfig.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,

        // camelCase to match last-run.json, which the plugin hand-writes without
        // System.Text.Json. --json output that disagrees with the file it reports on is
        // the kind of small inconsistency that costs someone an afternoon.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Names, not ordinals. Anything scripting against --json would otherwise break
        // silently the first time someone inserts a state in the middle of the enum.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Write via a temp file so an interrupted write cannot leave a half file.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
