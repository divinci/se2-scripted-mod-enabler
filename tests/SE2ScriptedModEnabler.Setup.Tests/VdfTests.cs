namespace SE2ScriptedModEnabler.Setup.Tests;

/// <summary>
/// Parsed against real files taken off this machine rather than hand-written samples,
/// because the thing that breaks a VDF reader is always some key Valve added that the
/// sample did not have.
/// </summary>
public class VdfTests
{
    [Fact]
    public void Reads_the_libraries_out_of_libraryfolders_vdf()
    {
        var root = Vdf.Parse(Fixture.Text("libraryfolders.vdf"))["libraryfolders"];

        Assert.NotNull(root);
        Assert.Equal(2, root!.Children.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", root["0"]!.ValueOf("path"));
        Assert.Equal(@"S:\steam", root["1"]!.ValueOf("path"));
    }

    [Fact]
    public void Knows_which_library_holds_the_game()
    {
        var root = Vdf.Parse(Fixture.Text("libraryfolders.vdf"))["libraryfolders"]!;

        Assert.Null(root["0"]!["apps"]![SteamCatalog.AppId]);
        Assert.Equal("76129006547", root["1"]!["apps"]![SteamCatalog.AppId]!.Value);
    }

    [Fact]
    public void Reads_installdir_and_buildid_out_of_the_app_manifest()
    {
        var app = Vdf.Parse(Fixture.Text($"appmanifest_{SteamCatalog.AppId}.acf"))["AppState"];

        Assert.NotNull(app);
        Assert.Equal("SpaceEngineers2", app!.ValueOf("installdir"));
        Assert.Equal("24225481", app.ValueOf("buildid"));
        Assert.Equal(SteamCatalog.AppId, app.ValueOf("appid"));
    }

    [Fact]
    public void Unescapes_the_backslashes_valve_doubles()
    {
        var app = Vdf.Parse(Fixture.Text($"appmanifest_{SteamCatalog.AppId}.acf"))["AppState"]!;

        Assert.Equal(@"C:\Program Files (x86)\Steam\steam.exe", app.ValueOf("LauncherPath"));
    }

    [Fact]
    public void Keeps_nested_blocks_addressable()
    {
        var depots = Vdf.Parse(Fixture.Text($"appmanifest_{SteamCatalog.AppId}.acf"))["AppState"]!["InstalledDepots"];

        Assert.NotNull(depots);
        Assert.NotEmpty(depots!.Children);
        Assert.All(depots.Children, depot => Assert.NotNull(depot.ValueOf("manifest")));
    }

    [Fact]
    public void Survives_comments_and_unquoted_tokens()
    {
        var root = Vdf.Parse("""
            // a comment Valve might add
            "top"
            {
                "a"     "1"
                b       2
                "nested" { "c" "3" }
            }
            """);

        var top = root["top"]!;
        Assert.Equal("1", top.ValueOf("a"));
        Assert.Equal("2", top.ValueOf("b"));
        Assert.Equal("3", top["nested"]!.ValueOf("c"));
    }

    [Fact]
    public void Does_not_throw_on_a_truncated_file()
    {
        var root = Vdf.Parse("\"top\"\n{\n  \"a\" \"1\"");

        Assert.Equal("1", root["top"]!.ValueOf("a"));
    }
}

public class HostPathsTests
{
    [Theory]
    [InlineData("/mnt/c/Users/x/y.dll", @"C:\Users\x\y.dll")]
    [InlineData("/mnt/s/steam/steamapps", @"S:\steam\steamapps")]
    public void Wsl_paths_become_windows_paths(string host, string windows)
    {
        Assert.Equal(OperatingSystem.IsWindows() ? host : windows, HostPaths.ToWindows(host));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam", "/mnt/c/Program Files (x86)/Steam")]
    [InlineData(@"S:\steam", "/mnt/s/steam")]
    public void Windows_paths_become_wsl_paths(string windows, string host)
    {
        Assert.Equal(OperatingSystem.IsWindows() ? windows : host, HostPaths.ToHost(windows));
    }

    [Theory]
    [InlineData(@"C:\a\b.dll", true)]
    [InlineData(@"\\server\share\b.dll", true)]
    [InlineData("/mnt/c/a/b.dll", false)]
    [InlineData("b.dll", false)]
    public void Recognises_windows_paths(string path, bool expected)
    {
        Assert.Equal(expected, HostPaths.LooksLikeWindowsPath(path));
    }
}

public class InstallerTests
{
    [Fact]
    public void A_stray_dll_in_the_install_folder_is_refused()
    {
        var dir = Directory.CreateTempSubdirectory("sme").FullName;
        try
        {
            Assert.Null(Installer.UnsafeDirReason(dir));

            File.WriteAllText(Path.Combine(dir, Files.PluginDll), "");
            Assert.Null(Installer.UnsafeDirReason(dir));

            // PluginHost puts this folder on the process-wide assembly resolve path.
            File.WriteAllText(Path.Combine(dir, "System.Text.Json.dll"), "");
            Assert.Contains("System.Text.Json.dll", Installer.UnsafeDirReason(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_install_folder_is_not_unsafe()
    {
        Assert.Null(Installer.UnsafeDirReason(Path.Combine(Path.GetTempPath(), "sme-does-not-exist")));
    }
}
