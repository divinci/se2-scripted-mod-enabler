using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SE2ScriptedModEnabler.Setup;

public sealed record InstallOptions
{
    /// <summary>The Game2 folder, or the folder above it. Null means go and find it.</summary>
    public string? GameDir { get; init; }

    /// <summary>Where the DLL lives. Null means %LOCALAPPDATA%\SE2ScriptedModEnabler.</summary>
    public string? InstallDir { get; init; }

    /// <summary>The built plugin to copy in. Null means the one shipped beside this tool.</summary>
    public string? PluginSource { get; init; }

    /// <summary>Work everything out and say what would happen, but write nothing.</summary>
    public bool DryRun { get; init; }
}

public sealed record ActionResult(bool Ok, string Summary, IReadOnlyList<string> Steps);

/// <summary>
/// Install, uninstall and inspect — the whole product, minus a face.
///
/// The install is two things on disk: a DLL in <c>%LOCALAPPDATA%</c>, and one appended
/// path in the game's <c>DEV_PLUGINS</c> runtime config property. Nothing is written
/// into the game folder, so a Steam "verify integrity of game files" cannot delete the
/// plugin — at worst it reverts the one-line config edit, which
/// <see cref="Inspect"/> notices and <see cref="Install"/> puts back.
/// </summary>
public static class Installer
{
    public static InstallReport Inspect(InstallOptions options)
    {
        var notes = new List<string>();

        var game = SteamCatalog.Locate(options.GameDir, out var trail);
        notes.AddRange(trail);

        if (game is null)
            return new InstallReport(InstallStatus.GameNotFound,
                "Space Engineers 2 was not found. Point at it with --game-dir.",
                null, null, null, false, false, false, [], null, null, notes);

        var installDir = ResolveInstallDir(options, notes);
        var record = installDir is null ? null : Files.ReadJson<InstallRecord>(Path.Combine(installDir, Files.InstallRecord));
        var lastRun = installDir is null ? null : Files.ReadJson<LastRun>(Path.Combine(installDir, Files.LastRun));

        byte[] config;
        try
        {
            config = File.ReadAllBytes(game.RuntimeConfigPath);
        }
        catch (IOException ex)
        {
            notes.Add($"could not read {game.RuntimeConfigPath}: {ex.Message}");
            return new InstallReport(InstallStatus.GameNotFound, "The game's runtimeconfig.json could not be read.",
                game, installDir, null, false, false, false, [], lastRun, record, notes);
        }

        var segments = RuntimeConfigPatcher.ReadSegments(config);
        var ourEntry = segments.FirstOrDefault(s =>
            string.Equals(RuntimeConfigPatcher.LeafName(s), Files.PluginDll, StringComparison.OrdinalIgnoreCase));

        var registered = ourEntry is not null;
        var pluginPath = installDir is null ? null : Path.Combine(installDir, Files.PluginDll);
        var pluginPresent = pluginPath is not null && File.Exists(pluginPath);

        var foreignEdit = registered
                          && record is not null
                          && !string.IsNullOrEmpty(record.PatchedSha256)
                          && record.PatchedSha256 != Files.Sha256(config);

        if (foreignEdit)
            notes.Add("runtimeconfig.json has changed since we edited it — probably an SE2 update. "
                    + "Our entry is still there, and uninstall will remove just that entry.");

        if (ourEntry is not null && pluginPath is not null
            && !string.Equals(ourEntry, HostPaths.ToWindows(pluginPath), StringComparison.OrdinalIgnoreCase))
            notes.Add($"DEV_PLUGINS points at {ourEntry}, which is not where this tool would install "
                    + $"({HostPaths.ToWindows(pluginPath)}). Run install to correct it.");

        var unsafeReason = installDir is null ? null : UnsafeDirReason(installDir);
        if (unsafeReason is not null) notes.Add(unsafeReason);

        var (status, headline) = Classify(registered, pluginPresent, unsafeReason is not null, lastRun, game);

        return new InstallReport(status, headline, game, installDir,
            pluginPath is null ? null : HostPaths.ToWindows(pluginPath),
            registered, pluginPresent, foreignEdit, segments, lastRun, record, notes);
    }

    private static (InstallStatus, string) Classify(
        bool registered, bool pluginPresent, bool unsafeDir, LastRun? lastRun, GameInstall game)
    {
        if (!registered)
            return (InstallStatus.NotInstalled, "Not installed. Script mods will not load.");

        if (!pluginPresent)
            return (InstallStatus.MissingDll,
                "Registered, but the plugin file is missing. Run install to put it back.");

        if (unsafeDir)
            return (InstallStatus.UnsafeDir,
                "Another DLL is in the plugin folder. Remove it before starting the game.");

        if (lastRun is null)
            return (InstallStatus.NeverRan, "Installed. Start Space Engineers 2 once to confirm it works.");

        var build = lastRun.GameBuild is null ? "" : $" (build {lastRun.GameBuild})";

        return lastRun.State switch
        {
            "working" => (InstallStatus.Working, $"Working{build}. Script mods will load."),
            "armed" => (InstallStatus.Armed,
                $"Armed{build}, but the last run never finished starting up. Try launching the game again."),
            "degraded" => (InstallStatus.Degraded,
                $"Partly working{build}. Something the plugin expected was not there — see the notes."),
            "paused" => (InstallStatus.Paused,
                $"Paused. Space Engineers 2 is on build {lastRun.GameBuild ?? "?"}, which this version has not been "
                + $"tested against (it knows {lastRun.SupportedBuilds ?? "?"}). The game is running unmodified. "
                + "Update SE2 Scripted Mod Enabler to re-enable script mods."),
            "failed" => (InstallStatus.Failed,
                $"The plugin hit an error{build} and stood down. The game still runs — see the notes."),
            "opted-out" => (InstallStatus.OptedOut, "Switched off by -noSme or SE2SME_DISABLE=1."),
            _ => (InstallStatus.Degraded, $"Unrecognised state '{lastRun.State}' from the last run."),
        };
    }

    public static ActionResult Install(InstallOptions options)
    {
        var steps = new List<string>();

        var game = SteamCatalog.Locate(options.GameDir, out var trail);
        if (game is null)
            return new ActionResult(false, "Space Engineers 2 was not found. Point at it with --game-dir.", trail);
        steps.Add($"game: {game.GameDir} (via {game.Source})");

        var installDir = ResolveInstallDir(options, steps);
        if (installDir is null)
            return new ActionResult(false, "Could not work out where to install. Pass --install-dir.", steps);
        steps.Add($"install dir: {installDir}");

        var source = ResolvePluginSource(options, steps);
        if (source is null)
            return new ActionResult(false,
                $"Could not find {Files.PluginDll} to install. Pass --plugin.", steps);

        var unsafeReason = UnsafeDirReason(installDir);
        if (unsafeReason is not null)
            return new ActionResult(false, unsafeReason, steps);

        var target = Path.Combine(installDir, Files.PluginDll);
        var windowsTarget = HostPaths.ToWindows(target);
        steps.Add($"plugin will be registered as: {windowsTarget}");

        byte[] config;
        try
        {
            config = File.ReadAllBytes(game.RuntimeConfigPath);
        }
        catch (IOException ex)
        {
            return new ActionResult(false, $"Could not read {game.RuntimeConfigPath}: {ex.Message}", steps);
        }

        var originalSha = Files.Sha256(config);
        steps.Add($"runtimeconfig.json before: {originalSha[..12]} ({config.Length} bytes)");

        var patch = RuntimeConfigPatcher.Add(config, windowsTarget);
        steps.Add($"patch: {patch.Outcome} — {patch.Detail}");
        if (!patch.Ok) return new ActionResult(false, patch.Detail, steps);

        var final = patch.Content ?? config;
        steps.Add($"runtimeconfig.json after: {Files.Sha256(final)[..12]} ({final.Length} bytes)");
        steps.Add($"DEV_PLUGINS will be: {string.Join(" ; ", patch.Segments)}");

        if (options.DryRun)
            return new ActionResult(true, "Dry run — nothing was written.", steps);

        try
        {
            Directory.CreateDirectory(installDir);

            // The backup lives with our files, not in the game folder: Steam should
            // never find a file it does not recognise next to its own.
            var backup = Path.Combine(installDir, Files.BackupDir, Files.RuntimeConfig);
            if (!File.Exists(backup))
            {
                Files.WriteAtomic(backup, config);
                steps.Add($"backed up the original to {backup}");
            }

            File.Copy(source, target, overwrite: true);
            steps.Add($"copied {source} -> {target}");

            if (patch.Content is not null)
            {
                Files.WriteAtomic(game.RuntimeConfigPath, patch.Content);
                steps.Add($"wrote {game.RuntimeConfigPath}");
            }

            Files.WriteAtomic(Path.Combine(installDir, Files.InstallRecord),
                JsonSerializer.SerializeToUtf8Bytes(new InstallRecord
                {
                    GameDir = game.GameDir,
                    PluginPath = target,
                    PluginPathWindows = windowsTarget,
                    PatchedSha256 = Files.Sha256(final),
                    OriginalSha256 = originalSha,
                    InstalledUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ToolVersion = ToolVersion(),
                }, Files.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionResult(false, $"Install failed: {ex.Message}", steps);
        }

        return new ActionResult(true,
            "Installed. Start Space Engineers 2 from Steam as usual — no launch options needed.", steps);
    }

    public static ActionResult Uninstall(InstallOptions options)
    {
        var steps = new List<string>();

        var game = SteamCatalog.Locate(options.GameDir, out var trail);
        if (game is null)
            return new ActionResult(false, "Space Engineers 2 was not found. Point at it with --game-dir.", trail);
        steps.Add($"game: {game.GameDir} (via {game.Source})");

        var installDir = ResolveInstallDir(options, steps);

        byte[] config;
        try
        {
            config = File.ReadAllBytes(game.RuntimeConfigPath);
        }
        catch (IOException ex)
        {
            return new ActionResult(false, $"Could not read {game.RuntimeConfigPath}: {ex.Message}", steps);
        }

        // Surgery, not restore-from-backup: SE2 may have changed this file since we
        // edited it, and putting our copy back would undo Keen's change as well as ours.
        var patch = RuntimeConfigPatcher.Remove(config, Files.PluginDll);
        steps.Add($"patch: {patch.Outcome} — {patch.Detail}");
        if (!patch.Ok) return new ActionResult(false, patch.Detail, steps);

        if (patch.Content is not null)
            steps.Add($"runtimeconfig.json after: {Files.Sha256(patch.Content)[..12]} ({patch.Content.Length} bytes)");

        if (options.DryRun)
            return new ActionResult(true, "Dry run — nothing was written.", steps);

        try
        {
            if (patch.Content is not null)
            {
                Files.WriteAtomic(game.RuntimeConfigPath, patch.Content);
                steps.Add($"wrote {game.RuntimeConfigPath}");
            }

            if (installDir is not null && Directory.Exists(installDir))
            {
                foreach (var name in (string[])[Files.PluginDll, Files.LastRun, Files.InstallRecord])
                {
                    var path = Path.Combine(installDir, name);
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    steps.Add($"deleted {path}");
                }

                var backupDir = Path.Combine(installDir, Files.BackupDir);
                if (Directory.Exists(backupDir))
                {
                    Directory.Delete(backupDir, recursive: true);
                    steps.Add($"deleted {backupDir}");
                }

                if (!Directory.EnumerateFileSystemEntries(installDir).Any())
                {
                    Directory.Delete(installDir);
                    steps.Add($"removed the empty {installDir}");
                }
                else
                {
                    steps.Add($"left {installDir} in place — it still has files we did not put there");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ActionResult(false, $"Uninstall failed: {ex.Message}", steps);
        }

        return new ActionResult(true, "Uninstalled. Space Engineers 2 is back to stock.", steps);
    }

    /// <summary>
    /// PluginHost.TryAddFromAssembly hooks AppDomain.AssemblyResolve onto the folder the
    /// plugin came from, for the life of the process. Any other DLL sitting there can
    /// therefore shadow a framework or game assembly — a failure that would look like a
    /// game bug and be blamed on the mod. Cheaper to refuse.
    /// </summary>
    public static string? UnsafeDirReason(string installDir)
    {
        if (!Directory.Exists(installDir)) return null;

        var strays = Directory.EnumerateFiles(installDir, "*.dll")
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, Files.PluginDll, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return strays.Count == 0
            ? null
            : $"{installDir} contains other DLLs ({string.Join(", ", strays)}). The game adds this folder to its "
            + "assembly resolve path, so they could shadow game or framework assemblies. Remove them first.";
    }

    private static string? ResolveInstallDir(InstallOptions options, List<string> notes)
    {
        if (!string.IsNullOrWhiteSpace(options.InstallDir)) return HostPaths.ToHost(options.InstallDir);

        var dir = HostPaths.DefaultInstallDir(out var detail);
        if (dir is null) notes.Add($"install dir: {detail}");
        return dir;
    }

    private static string? ResolvePluginSource(InstallOptions options, List<string> notes)
    {
        if (!string.IsNullOrWhiteSpace(options.PluginSource))
        {
            var given = HostPaths.ToHost(options.PluginSource);
            if (File.Exists(given)) return given;
            notes.Add($"--plugin {given}: not found");
            return null;
        }

        var beside = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location ?? Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(beside)) return null;

        var candidate = Path.Combine(beside, Files.PluginDll);
        if (File.Exists(candidate)) return candidate;

        notes.Add($"no {Files.PluginDll} beside the tool ({beside})");

        var developing = FromSourceTree(beside);
        if (developing is not null)
        {
            notes.Add($"using the build output instead: {developing}");
            return developing;
        }

        return null;
    }

    /// <summary>
    /// Shipped, the plugin sits beside the installer and the check above finds it. Run
    /// from the source tree it does not — the two projects build to their own bin
    /// folders — and requiring --plugin for every spike step is an invitation to install
    /// yesterday's DLL by accident. Returns null outside a checkout, so this cannot
    /// change what a released build does.
    /// </summary>
    private static string? FromSourceTree(string startingAt)
    {
        var dir = new DirectoryInfo(startingAt);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "se2sme.sln")))
            dir = dir.Parent;
        if (dir is null) return null;

        foreach (var config in new[] { "Release", "Debug" })
        {
            var built = Path.Combine(dir.FullName, "src", "SE2ScriptedModEnabler", "bin", config, "net9.0", Files.PluginDll);
            if (File.Exists(built)) return built;
        }

        return null;
    }

    private static string ToolVersion() =>
        typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Installer).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
