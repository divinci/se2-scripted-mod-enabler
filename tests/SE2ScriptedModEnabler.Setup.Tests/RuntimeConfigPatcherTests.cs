using System.Text;

namespace SE2ScriptedModEnabler.Setup.Tests;

/// <summary>
/// The patcher edits a file Steam owns, on a machine we will never see, belonging to
/// someone who cannot be asked to fix it. So the bar is not "produces equivalent JSON"
/// but "produces the same bytes, apart from the ones we meant to change" — and after an
/// uninstall, the same bytes full stop.
/// </summary>
public class RuntimeConfigPatcherTests
{
    private const string Plugin = @"C:\Users\Someone\AppData\Local\SE2ScriptedModEnabler\SE2ScriptedModEnabler.dll";
    private const string Stock = @"..\Game2.ContentBuilder\Game2.ContentBuilder.csproj";

    [Fact]
    public void The_fixture_is_the_real_shipping_file()
    {
        // If this fails, git has normalised the fixture and every byte-exactness test
        // below is measuring the wrong thing.
        var content = Fixture.RuntimeConfig();
        Assert.Equal(Fixture.RuntimeConfigSha, Files.Sha256(content));
        Assert.Equal(745, content.Length);
        Assert.DoesNotContain("\n"u8.ToArray()[0], content[^1..]);   // no trailing newline
    }

    [Fact]
    public void Reads_the_stock_dev_plugins_entry()
    {
        Assert.Equal([Stock], RuntimeConfigPatcher.ReadSegments(Fixture.RuntimeConfig()));
    }

    [Fact]
    public void Add_then_remove_is_byte_identical()
    {
        var original = Fixture.RuntimeConfig();

        var added = RuntimeConfigPatcher.Add(original, Plugin);
        Assert.Equal(PatchOutcome.Added, added.Outcome);
        Assert.NotNull(added.Content);

        var removed = RuntimeConfigPatcher.Remove(added.Content!, Files.PluginDll);
        Assert.Equal(PatchOutcome.Removed, removed.Outcome);
        Assert.Equal(original, removed.Content);
    }

    [Fact]
    public void Add_changes_exactly_one_line()
    {
        var original = Encoding.UTF8.GetString(Fixture.RuntimeConfig());
        var patched = Encoding.UTF8.GetString(RuntimeConfigPatcher.Add(Fixture.RuntimeConfig(), Plugin).Content!);

        var before = original.Split("\r\n");
        var after = patched.Split("\r\n");

        Assert.Equal(before.Length, after.Length);
        var differing = before.Zip(after).Count(pair => pair.First != pair.Second);
        Assert.Equal(1, differing);
    }

    [Fact]
    public void Add_preserves_crlf_and_the_absent_trailing_newline()
    {
        var patched = RuntimeConfigPatcher.Add(Fixture.RuntimeConfig(), Plugin).Content!;
        var text = Encoding.UTF8.GetString(patched);

        Assert.Equal(text.Count(c => c == '\r'), text.Count(c => c == '\n'));
        Assert.DoesNotContain("\n\n", text);
        Assert.EndsWith("}", text);
        Assert.False(text.EndsWith("\n"));
        Assert.NotEqual(0xEF, patched[0]);
    }

    [Fact]
    public void Add_preserves_a_bom_when_there_is_one()
    {
        var withBom = Fixture.WithBom(Fixture.RuntimeConfig());
        var patched = RuntimeConfigPatcher.Add(withBom, Plugin);

        Assert.Equal(PatchOutcome.Added, patched.Outcome);
        Assert.Equal([0xEF, 0xBB, 0xBF], patched.Content![..3]);
        Assert.Equal([Stock, Plugin], patched.Segments);

        // And back out again, byte for byte.
        Assert.Equal(withBom, RuntimeConfigPatcher.Remove(patched.Content!, Files.PluginDll).Content);
    }

    [Fact]
    public void Non_ascii_earlier_in_the_file_does_not_shift_the_splice()
    {
        // Byte offsets and char offsets diverge from the first multi-byte character.
        // A user named Sørensen is enough to expose it, and only on their machine.
        var text = Encoding.UTF8.GetString(Fixture.RuntimeConfig())
            .Replace("\"tfm\": \"net9.0\"", "\"tfm\": \"nét9.0—ø\"");
        var original = Encoding.UTF8.GetBytes(text);

        var added = RuntimeConfigPatcher.Add(original, Plugin);

        Assert.Equal(PatchOutcome.Added, added.Outcome);
        Assert.Equal([Stock, Plugin], added.Segments);
        Assert.Equal(original, RuntimeConfigPatcher.Remove(added.Content!, Files.PluginDll).Content);
    }

    [Fact]
    public void Adding_twice_is_a_no_op()
    {
        var once = RuntimeConfigPatcher.Add(Fixture.RuntimeConfig(), Plugin).Content!;
        var twice = RuntimeConfigPatcher.Add(once, Plugin);

        Assert.Equal(PatchOutcome.AlreadyPresent, twice.Outcome);
        Assert.Null(twice.Content);
    }

    [Fact]
    public void A_stale_entry_for_our_dll_is_corrected_not_duplicated()
    {
        var stale = RuntimeConfigPatcher.Add(Fixture.RuntimeConfig(), @"D:\old\SE2ScriptedModEnabler.dll").Content!;
        var fixedUp = RuntimeConfigPatcher.Add(stale, Plugin);

        Assert.Equal(PatchOutcome.Replaced, fixedUp.Outcome);
        Assert.Equal([Stock, Plugin], fixedUp.Segments);
    }

    [Fact]
    public void Remove_takes_stale_entries_with_it()
    {
        var stale = RuntimeConfigPatcher.Add(Fixture.RuntimeConfig(), @"D:\old\SE2ScriptedModEnabler.dll").Content!;
        var removed = RuntimeConfigPatcher.Remove(stale, Files.PluginDll);

        Assert.Equal(PatchOutcome.Removed, removed.Outcome);
        Assert.Equal(Fixture.RuntimeConfig(), removed.Content);
    }

    [Fact]
    public void Remove_leaves_a_stock_file_alone()
    {
        var removed = RuntimeConfigPatcher.Remove(Fixture.RuntimeConfig(), Files.PluginDll);

        Assert.Equal(PatchOutcome.NotPresent, removed.Outcome);
        Assert.Null(removed.Content);
    }

    [Fact]
    public void Remove_keeps_an_entry_added_by_something_else()
    {
        var text = Encoding.UTF8.GetString(Fixture.RuntimeConfig())
            .Replace(Stock.Replace(@"\", @"\\"),
                     Stock.Replace(@"\", @"\\") + @";C:\\Other\\SomeoneElsesPlugin.dll");
        var shared = RuntimeConfigPatcher.Add(Encoding.UTF8.GetBytes(text), Plugin).Content!;

        var removed = RuntimeConfigPatcher.Remove(shared, Files.PluginDll);

        Assert.Equal(PatchOutcome.Removed, removed.Outcome);
        Assert.Equal([Stock, @"C:\Other\SomeoneElsesPlugin.dll"], removed.Segments);
        Assert.Equal(Encoding.UTF8.GetBytes(text), removed.Content);
    }

    [Theory]
    [InlineData(@"/mnt/c/Users/x/SE2ScriptedModEnabler.dll", "must be a Windows path")]
    [InlineData(@"C:\a;b\SE2ScriptedModEnabler.dll", "separator")]
    [InlineData(@"C:\x\SE2ScriptedModEnabler.exe", "must name a .dll")]
    [InlineData("", "empty")]
    public void Refuses_paths_the_game_could_not_load(string path, string because)
    {
        var result = RuntimeConfigPatcher.Add(Fixture.RuntimeConfig(), path);

        Assert.Equal(PatchOutcome.Unsupported, result.Outcome);
        Assert.Contains(because, result.Detail);
        Assert.Null(result.Content);
    }

    [Fact]
    public void Creates_the_key_when_the_file_has_no_dev_plugins()
    {
        var text = Encoding.UTF8.GetString(Fixture.RuntimeConfig());
        var line = text.Split("\r\n").Single(l => l.Contains(RuntimeConfigPatcher.Key));
        var without = Encoding.UTF8.GetBytes(text.Replace(line + "\r\n", ""));

        var added = RuntimeConfigPatcher.Add(without, Plugin);

        Assert.Equal(PatchOutcome.KeyInserted, added.Outcome);
        Assert.Equal([Plugin], added.Segments);

        // The key it created stays behind, emptied. Remove is a pure function of the
        // file and cannot tell a key it added from one Keen shipped empty; to the game
        // the two are the same, since LoadPlugins splits with RemoveEmptyEntries.
        var removed = RuntimeConfigPatcher.Remove(added.Content!, Files.PluginDll);
        Assert.Equal(PatchOutcome.Removed, removed.Outcome);
        Assert.Empty(removed.Segments);
        Assert.Contains($"\"{RuntimeConfigPatcher.Key}\": \"\"", Encoding.UTF8.GetString(removed.Content!));
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_runtimeconfig()
    {
        var result = RuntimeConfigPatcher.Add(Fixture.Utf8("""{"runtimeOptions":{"tfm":"net9.0"}}"""), Plugin);

        Assert.Equal(PatchOutcome.Unsupported, result.Outcome);
        Assert.Contains("configProperties", result.Detail);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_json()
    {
        var result = RuntimeConfigPatcher.Add(Fixture.Utf8("not json at all"), Plugin);

        Assert.Equal(PatchOutcome.Invalid, result.Outcome);
        Assert.Null(result.Content);
    }

    [Fact]
    public void Refuses_a_dev_plugins_value_that_is_not_a_string()
    {
        var text = Encoding.UTF8.GetString(Fixture.RuntimeConfig())
            .Replace($"\"{RuntimeConfigPatcher.Key}\": \"..\\\\Game2.ContentBuilder\\\\Game2.ContentBuilder.csproj\"",
                     $"\"{RuntimeConfigPatcher.Key}\": null");

        var result = RuntimeConfigPatcher.Add(Encoding.UTF8.GetBytes(text), Plugin);

        Assert.Equal(PatchOutcome.Unsupported, result.Outcome);
        Assert.Contains("not a string", result.Detail);
    }

    [Fact]
    public void Refuses_to_edit_a_value_with_escaped_semicolons()
    {
        // \u003b is a separator to the game but not to a raw text split, so the two
        // views of the value would disagree about how many entries there are.
        var text = Encoding.UTF8.GetString(Fixture.RuntimeConfig())
            .Replace(@"..\\Game2.ContentBuilder", @"..\u003bGame2.ContentBuilder");

        var result = RuntimeConfigPatcher.Add(Encoding.UTF8.GetBytes(text), Plugin);

        Assert.Equal(PatchOutcome.Unsupported, result.Outcome);
        Assert.Contains("escaped semicolon", result.Detail);
    }

    [Fact]
    public void Handles_a_file_whose_dev_plugins_is_empty()
    {
        var text = Encoding.UTF8.GetString(Fixture.RuntimeConfig())
            .Replace(@"..\\Game2.ContentBuilder\\Game2.ContentBuilder.csproj", "");
        var empty = Encoding.UTF8.GetBytes(text);

        var added = RuntimeConfigPatcher.Add(empty, Plugin);

        Assert.Equal(PatchOutcome.Added, added.Outcome);
        Assert.Equal([Plugin], added.Segments);
        Assert.Equal(empty, RuntimeConfigPatcher.Remove(added.Content!, Files.PluginDll).Content);
    }

    [Fact]
    public void A_username_needing_json_escapes_survives_the_round_trip()
    {
        const string awkward = @"C:\Users\Ann & Bo\AppData\Local\SE2ScriptedModEnabler\SE2ScriptedModEnabler.dll";

        var added = RuntimeConfigPatcher.Add(Fixture.RuntimeConfig(), awkward);

        Assert.Equal(PatchOutcome.Added, added.Outcome);
        Assert.Equal([Stock, awkward], added.Segments);
        Assert.Equal(Fixture.RuntimeConfig(), RuntimeConfigPatcher.Remove(added.Content!, Files.PluginDll).Content);
    }
}
