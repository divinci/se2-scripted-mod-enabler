using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SE2ScriptedModEnabler.Tests;

internal static class Repo
{
    public const string Manifest = "SE2ScriptedModEnabler.xml";
    public const string RepoId = "divinci/se2-scripted-mod-enabler";
    public const string PluginDir = "src/ClientPlugin";

    /// <summary>The manifest doubles as the repo-root marker; nothing else is unique.</summary>
    public static readonly string Root = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Manifest)))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public static XElement Plugin() => XDocument.Load(Path.Combine(Root, Manifest)).Root!;

    public static string? Text(this XElement root, string name) => root.Element(name)?.Value;

    /// <summary>
    /// Every .cs file Pulsar would consider, before SourceDirectories narrows it. Pulsar's
    /// dev-folder path asks git and falls back to walking the tree minus bin/obj; the
    /// GitHub path walks the extracted archive. This is the fallback rule, which agrees
    /// with the other two because .gitignore covers bin and obj.
    /// </summary>
    public static IEnumerable<string> AllSources()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            if (relative.Split('/').Any(s => s is "bin" or "obj" or ".git")) continue;
            yield return relative;
        }
    }
}

/// <summary>
/// The hub manifest, checked here because nothing else will.
///
/// <para>PluginHub-SE2's own workflow is guarded by
/// <c>if: github.repository == 'StarCpt/PluginHub-SE2'</c>, so on a fork — which is where
/// the submission is prepared — CI passes without running <c>test.py</c> at all. The first
/// half of this file is that script, reimplemented. The second half is the part
/// <c>test.py</c> could not check even if it ran: that <c>SourceDirectories</c> selects
/// exactly the files this repository intends to ship. Nothing else catches a new .cs file
/// added outside the plugin folder — it builds locally and fails only on the player's
/// machine, after the hub has merged it.</para>
/// </summary>
public class ManifestTests
{
    [Fact]
    public void It_is_the_plugin_type_the_hub_accepts()
    {
        var plugin = Repo.Plugin();

        Assert.Equal("PluginData", plugin.Name.LocalName);

        var xsi = (XNamespace)"http://www.w3.org/2001/XMLSchema-instance";
        Assert.Equal("GitHubPlugin", plugin.Attribute(xsi + "type")?.Value);
    }

    [Fact]
    public void It_has_the_fields_test_py_demands()
    {
        var plugin = Repo.Plugin();

        Assert.False(string.IsNullOrWhiteSpace(plugin.Text("Id")));
        Assert.False(string.IsNullOrWhiteSpace(plugin.Text("FriendlyName")));
        Assert.False(string.IsNullOrWhiteSpace(plugin.Text("Author")));
        Assert.False(string.IsNullOrWhiteSpace(plugin.Text("Commit")));
    }

    [Fact]
    public void The_id_is_a_real_guid_and_never_changes()
    {
        var id = Repo.Plugin().Text("Id");

        Assert.True(Guid.TryParse(id, out var guid), $"'{id}' is not a GUID");

        // The template ships all-zeros, and a plugin that keeps it collides with every
        // other plugin that did the same. Pulsar keys enabled state and the data folder
        // off this, so it is also the one field a release must never touch.
        Assert.NotEqual(Guid.Empty, guid);
    }

    [Fact]
    public void The_commit_is_a_full_hash()
    {
        var commit = Repo.Plugin().Text("Commit")!;

        // test.py only asks for lowercase hex, which an abbreviated hash satisfies. Pulsar
        // downloads https://github.com/<RepoId>/archive/<Commit>.zip, so an abbreviation
        // works right up until it becomes ambiguous.
        Assert.Matches("^[0-9a-f]{40}$", commit);
    }

    [Fact]
    public void The_description_fits_the_details_panel()
    {
        var description = Repo.Plugin().Text("Description")!;

        Assert.NotEmpty(description);
        Assert.True(description.Length <= 1000, $"{description.Length} characters, the limit is 1000");

        // The plugin binds to the game at compile time, so a game update can stop it
        // building and Pulsar will disable it. That is a good outcome — the failure is
        // caught before the game starts — but it is still a plugin that stopped working,
        // and a details panel that never mentions updates turns it into a bug report.
        Assert.Contains("update", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_repo_id_is_this_repository()
    {
        Assert.Equal(Repo.RepoId, Repo.Plugin().Text("RepoId"));

        var config = GitConfig();
        if (config is null) return;   // a source archive, not a clone

        var origin = Regex.Match(config, @"url\s*=\s*(?<url>\S+)");
        Assert.True(origin.Success, "no remote url in .git/config");

        // Pulsar builds every download URL from RepoId. Point it at the wrong repo and the
        // hub happily serves someone else's code under this plugin's name.
        Assert.Contains(Repo.RepoId, origin.Groups["url"].Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GitConfig()
    {
        var dotGit = Path.Combine(Repo.Root, ".git");

        // A submodule checkout has .git as a file pointing at the real directory.
        if (File.Exists(dotGit))
        {
            var pointer = File.ReadAllText(dotGit).Trim();
            if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
            dotGit = Path.GetFullPath(Path.Combine(Repo.Root, pointer["gitdir:".Length..].Trim()));
        }

        var config = Path.Combine(dotGit, "config");
        return File.Exists(config) ? File.ReadAllText(config) : null;
    }

    private static string[] SourceDirectories() =>
        Repo.Plugin().Element("SourceDirectories")!
            .Elements("Directory")
            .Select(d => d.Value.Replace('\\', '/').TrimStart('/'))
            .ToArray();

    /// <summary><c>GitHubPlugin.CleanPaths</c> then <c>IsValidProjectFile</c>.</summary>
    private static string[] SelectedByTheHub() =>
        Repo.AllSources()
            .Where(f => SourceDirectories().Any(d => f.StartsWith(d.TrimEnd('/') + "/", StringComparison.Ordinal)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// <c>LocalFolderPlugin.IsValidProjectFile</c>, which does <em>not</em> append the
    /// trailing slash its GitHub counterpart does.
    /// </summary>
    private static string[] SelectedByADevFolder() =>
        Repo.AllSources()
            .Where(f => SourceDirectories().Any(d => f.StartsWith(d.TrimEnd('/'), StringComparison.Ordinal)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void A_dev_folder_and_the_hub_compile_the_same_files()
    {
        // CleanPaths appends '/' before matching; LocalFolderPlugin does not. So a sibling
        // named ClientPluginTests would be invisible to the hub and swept into a local
        // build — the two would diverge exactly where it is hardest to notice, because the
        // local build is the one you test with.
        Assert.Equal(SelectedByTheHub(), SelectedByADevFolder());
    }

    [Fact]
    public void Only_the_plugin_is_compiled()
    {
        var selected = SelectedByTheHub();

        Assert.NotEmpty(selected);
        Assert.All(selected, f => Assert.StartsWith(Repo.PluginDir + "/", f));

        // tests/ pulls in xunit, Mono.Cecil and the hand-written game stubs. Pulsar
        // compiles with a fixed reference set and no NuGet restore, so this is not a build
        // that fails cleanly — it fails on the player's machine.
        var everything = Repo.AllSources().ToArray();
        Assert.Contains(everything, f => f.StartsWith("tests/", StringComparison.Ordinal));
        Assert.DoesNotContain(selected, f => f.StartsWith("tests/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_manifest_and_the_csproj_agree_on_what_the_plugin_is()
    {
        var csproj = XDocument.Load(Path.Combine(Repo.Root, "src", "ClientPlugin", "ClientPlugin.csproj"));

        // The csproj is a developer convenience — Pulsar never reads it — so the only way
        // the two stay in step is if it takes the SDK's default glob over its own
        // directory, which is the same rule SourceDirectories expresses.
        Assert.DoesNotContain(csproj.Descendants(), e => e.Name.LocalName == "Compile");
        Assert.DoesNotContain(csproj.Descendants(), e => e.Name.LocalName == "EnableDefaultCompileItems");

        Assert.Equal(
            Repo.AllSources().Where(f => f.StartsWith(Repo.PluginDir + "/", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal),
            SelectedByTheHub());
    }

    [Fact]
    public void Nothing_asks_pulsar_for_extra_machinery()
    {
        var plugin = Repo.Plugin();

        // A NuGet package loads through Pulsar's own AssemblyResolver, which would put a
        // second copy of anything the game already has in the process — and would break
        // the PulsarLog binding this plugin depends on being resolver-free. An AssetFolder
        // would make Pulsar reflect for LoadAssets(string), which does not exist here.
        Assert.Null(plugin.Element("NuGetReferences"));
        Assert.Null(plugin.Element("Config"));
        Assert.Null(plugin.Element("AssetFolder"));
        Assert.Null(plugin.Element("DependencyIds"));
    }
}
