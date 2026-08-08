using System;
using System.Collections.Generic;
using System.IO;

namespace SE2ScriptedModEnabler.Setup;

/// <summary>Where Space Engineers 2 is, and which build Steam thinks is installed.</summary>
/// <param name="GameRoot">The <c>steamapps/common/SpaceEngineers2</c> folder.</param>
/// <param name="GameDir">The <c>Game2</c> folder — the exe, the DLLs, the runtimeconfig.</param>
/// <param name="BuildId">Steam's <c>buildid</c>, or null if the manifest was not read.</param>
public sealed record GameInstall(string GameRoot, string GameDir, string? BuildId, string Source)
{
    public string RuntimeConfigPath => Path.Combine(GameDir, "SpaceEngineers2.runtimeconfig.json");
    public string GameAssemblyPath => Path.Combine(GameDir, "SpaceEngineers2.dll");
}

/// <summary>
/// Finds the install by reading Steam's own bookkeeping rather than guessing:
/// <c>libraryfolders.vdf</c> lists every library and, usefully, the app ids each one
/// holds, so the right library is known before any directory is walked.
/// <c>appmanifest_1133870.acf</c> then gives <c>installdir</c> and <c>buildid</c>.
///
/// Steam's own root is still a guess — the reliable answer lives in the registry under
/// <c>HKCU\Software\Valve\Steam\SteamPath</c>, which needs a Windows target framework.
/// The WinForms shell will read it; until then this walks a short list of the usual
/// places, and <c>--game-dir</c> always wins.
/// </summary>
public static class SteamCatalog
{
    public const string AppId = "1133870";
    public const string GameSubdirectory = "Game2";

    /// <summary>Accepts either the Game2 folder or the folder above it.</summary>
    public static GameInstall? FromDirectory(string directory, string source)
    {
        directory = directory.TrimEnd('/', '\\');

        if (File.Exists(Path.Combine(directory, "SpaceEngineers2.runtimeconfig.json")))
            return new GameInstall(Path.GetDirectoryName(directory) ?? directory, directory, null, source);

        var nested = Path.Combine(directory, GameSubdirectory);
        if (File.Exists(Path.Combine(nested, "SpaceEngineers2.runtimeconfig.json")))
            return new GameInstall(directory, nested, null, source);

        return null;
    }

    public static GameInstall? Locate(string? explicitDirectory, out List<string> trail)
    {
        trail = [];

        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            var host = HostPaths.ToHost(explicitDirectory);
            var given = FromDirectory(host, "--game-dir");
            trail.Add(given is null
                ? $"--game-dir {host}: no SpaceEngineers2.runtimeconfig.json there or in {GameSubdirectory}/"
                : $"--game-dir {host}: found");
            return given;
        }

        var fromEnv = Environment.GetEnvironmentVariable("SE2_GAMEDIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var host = HostPaths.ToHost(fromEnv);
            var given = FromDirectory(host, "SE2_GAMEDIR");
            trail.Add(given is null ? $"SE2_GAMEDIR {host}: not a game folder" : $"SE2_GAMEDIR {host}: found");
            if (given is not null) return given;
        }

        foreach (var root in SteamRoots())
        {
            var vdf = FirstExisting(
                Path.Combine(root, "steamapps", "libraryfolders.vdf"),
                Path.Combine(root, "config", "libraryfolders.vdf"));

            if (vdf is null) { trail.Add($"{root}: no libraryfolders.vdf"); continue; }

            trail.Add($"{root}: reading {Path.GetFileName(vdf)}");

            foreach (var library in LibrariesHolding(vdf, AppId, trail))
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
                if (!File.Exists(manifest)) { trail.Add($"  {library}: no appmanifest_{AppId}.acf"); continue; }

                var app = Vdf.Parse(File.ReadAllText(manifest))["AppState"];
                var installDir = app?.ValueOf("installdir");
                var buildId = app?.ValueOf("buildid");
                if (string.IsNullOrWhiteSpace(installDir)) { trail.Add($"  {manifest}: no installdir"); continue; }

                var gameRoot = Path.Combine(library, "steamapps", "common", installDir);
                var install = FromDirectory(gameRoot, $"Steam library {library}");
                if (install is null) { trail.Add($"  {gameRoot}: manifest says installed, folder is not"); continue; }

                trail.Add($"  {install.GameDir}: found, Steam buildid {buildId}");
                return install with { BuildId = buildId };
            }
        }

        return null;
    }

    /// <summary>
    /// Libraries that libraryfolders.vdf says hold this app, most specific first.
    /// Falling back to every library covers the case where the apps list is stale.
    /// </summary>
    private static List<string> LibrariesHolding(string vdfPath, string appId, List<string> trail)
    {
        var holding = new List<string>();
        var others = new List<string>();

        VdfNode root;
        try { root = Vdf.Parse(File.ReadAllText(vdfPath)); }
        catch (IOException ex) { trail.Add($"  {vdfPath}: unreadable ({ex.Message})"); return holding; }

        foreach (var entry in (root["libraryfolders"] ?? root).Children)
        {
            var path = entry.ValueOf("path");
            if (string.IsNullOrWhiteSpace(path)) continue;

            var host = HostPaths.ToHost(path);
            if (entry["apps"]?[appId] is not null) holding.Add(host); else others.Add(host);
        }

        holding.AddRange(others);
        return holding;
    }

    private static IEnumerable<string> SteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var configured = Environment.GetEnvironmentVariable("SE2SME_STEAM_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && seen.Add(configured))
            yield return HostPaths.ToHost(configured);

        if (HostPaths.IsWindows)
        {
            foreach (var variable in (string[])["ProgramFiles(x86)", "ProgramFiles"])
            {
                var baseDir = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrWhiteSpace(baseDir)) continue;
                var candidate = Path.Combine(baseDir, "Steam");
                if (seen.Add(candidate)) yield return candidate;
            }

            foreach (var drive in DriveInfo.GetDrives())
            foreach (var name in (string[])["Steam", "SteamLibrary"])
            {
                var candidate = Path.Combine(drive.Name, name);
                if (seen.Add(candidate)) yield return candidate;
            }

            yield break;
        }

        if (!HostPaths.IsWsl) yield break;

        // WSL: /mnt/<letter> for whatever is mounted, plus the two names Steam uses.
        foreach (var mount in SafeDirectories("/mnt"))
        foreach (var name in (string[])["Steam", "steam", "SteamLibrary"])
        {
            var candidate = Path.Combine(mount, name);
            if (Directory.Exists(candidate) && seen.Add(candidate)) yield return candidate;
        }

        foreach (var mount in SafeDirectories("/mnt"))
        {
            var candidate = Path.Combine(mount, "Program Files (x86)", "Steam");
            if (Directory.Exists(candidate) && seen.Add(candidate)) yield return candidate;
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static string? FirstExisting(params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }
}
