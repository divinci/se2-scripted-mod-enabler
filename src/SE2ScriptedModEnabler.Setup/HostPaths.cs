using System;
using System.Globalization;
using System.IO;

namespace SE2ScriptedModEnabler.Setup;

/// <summary>
/// Translation between the paths this process opens and the paths the game will read.
///
/// On Windows — which is the only place the shipped installer runs — every method here
/// is the identity. It exists for development and for the spike: the work happens from
/// WSL against <c>/mnt/s/...</c>, while the value written into
/// <c>DEV_PLUGINS</c> has to be a Windows path such as <c>S:\steam\...</c>, because a
/// <c>/mnt/</c> path makes Assembly.Load fail silently — no plugin, no log line, no clue.
/// Getting this backwards is the most likely way to spend an evening debugging nothing.
/// </summary>
public static class HostPaths
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>Running under WSL with Windows drives mounted at /mnt/&lt;letter&gt;.</summary>
    public static bool IsWsl => !IsWindows && Directory.Exists("/mnt/c");

    /// <summary>A Windows path, as the game will see it, from a path this process can open.</summary>
    public static string ToWindows(string path)
    {
        if (IsWindows) return path;

        if (path.StartsWith("/mnt/", StringComparison.Ordinal) && path.Length >= 7 && path[6] == '/')
        {
            var drive = char.ToUpperInvariant(path[5]);
            return drive + ":" + path[6..].Replace('/', '\\');
        }

        return path;
    }

    /// <summary>A path this process can open, from a Windows path out of a Steam file.</summary>
    public static string ToHost(string path)
    {
        if (IsWindows) return path;

        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
            return "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');

        return path;
    }

    public static bool LooksLikeWindowsPath(string path) =>
        (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// <c>%LOCALAPPDATA%\SE2ScriptedModEnabler</c>, as a path this process can open.
    ///
    /// Under WSL, <see cref="Environment.SpecialFolder.LocalApplicationData"/> resolves
    /// to the Linux <c>~/.local/share</c>, which the game will never look at, so the
    /// Windows profile is located instead — and if that cannot be done unambiguously
    /// the caller is told to pass the directory rather than being handed a wrong guess.
    /// </summary>
    public static string? DefaultInstallDir(out string detail)
    {
        const string leaf = "SE2ScriptedModEnabler";

        var explicitDir = Environment.GetEnvironmentVariable("SE2SME_INSTALL_DIR");
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            detail = "SE2SME_INSTALL_DIR";
            return explicitDir;
        }

        if (IsWindows)
        {
            detail = "%LOCALAPPDATA%";
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), leaf);
        }

        if (IsWsl)
        {
            var users = "/mnt/c/Users";
            var me = Environment.UserName;
            var guess = Path.Combine(users, me, "AppData", "Local");
            if (Directory.Exists(guess))
            {
                detail = $"WSL, matched the Windows profile {me}";
                return Path.Combine(guess, leaf);
            }

            var found = (string?)null;
            var ambiguous = false;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(users))
                {
                    var name = Path.GetFileName(dir);
                    if (name is "Default" or "Default User" or "Public" or "All Users") continue;
                    if (!Directory.Exists(Path.Combine(dir, "AppData", "Local"))) continue;
                    if (found is not null) { ambiguous = true; break; }
                    found = dir;
                }
            }
            catch (IOException)
            {
                // Fall through to the "say so" branch below.
            }

            if (found is not null && !ambiguous)
            {
                detail = $"WSL, only Windows profile is {Path.GetFileName(found)}";
                return Path.Combine(found, "AppData", "Local", leaf);
            }

            detail = ambiguous
                ? "WSL with more than one Windows profile — pass --install-dir"
                : "WSL and no Windows profile found — pass --install-dir";
            return null;
        }

        detail = "not Windows — pass --install-dir";
        return null;
    }

    public static string Describe(long bytes) =>
        bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
}
