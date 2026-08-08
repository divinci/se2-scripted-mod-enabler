using Keen.Game2;
using Keen.Game2.Simulation.Scripting;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Project;
using Keen.VRage.DCS.Builders;
using Keen.VRage.Library.Utils;

namespace SE2ScriptedModEnabler.Tests;

/// <summary>
/// The plugin's actual effect, driven through the same event the game raises, against
/// stubs that misbehave the way the game does.
///
/// <para>Everything else in this suite reads metadata: it can say the plugin binds to
/// <c>AddScripting</c> and catches around the handler, and nothing more. What it cannot say
/// is whether calling those things in that order leaves scripting on — which is the only
/// claim the plugin makes to a player. That gap is what these cover, and it exists only
/// because <c>tests/GameStubs/SpaceEngineers2</c> reproduces the two defects that matter:
/// <c>AddScripting</c> throws if it runs twice, and it seeds both whitelists with an anchor
/// list that names one assembly twice.</para>
///
/// <para>They are not a substitute for a GAMEDIR build. A stub agrees with whatever was
/// written into it.</para>
/// </summary>
[Collection(GameStaticsCollection.Name)]
public class EngineSeamTests : IDisposable
{
    private readonly Type[] modAnchors = GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies;
    private readonly Type[] inGameAnchors = GameWhitelistProvider<InGameWhitelistProvider>.AllowedAssemblies;

    /// <summary>Both whitelists are process-wide mutable statics, in the game too.</summary>
    public void Dispose()
    {
        GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies = modAnchors;
        GameWhitelistProvider<InGameWhitelistProvider>.AllowedAssemblies = inGameAnchors;
    }

    /// <summary>
    /// An engine mid-construction, shaped the way <c>GameApp.AddScripting</c> expects to
    /// find it: a component list with the ProjectManager object builder somewhere in it,
    /// and something else first so a search that returns the head would pass by accident.
    /// </summary>
    private static EngineBuilder EngineWithProjectManager(out ProjectManagerEngineComponent.ObjectBuilder projectManager)
    {
        projectManager = new ProjectManagerEngineComponent.ObjectBuilder();

        var engine = new EngineBuilder();
        engine.EntityBuilder.Components =
        [
            new EntityBuilder.ComponentBuildInfo { ObjectBuilder = "something else entirely" },
            new EntityBuilder.ComponentBuildInfo { ObjectBuilder = projectManager },
        ];

        return engine;
    }

    private static void AssertDedupedFrom(Type[] before, Type[] after)
    {
        Assert.NotEmpty(before);

        // The claim is narrow on purpose: keep the first anchor of each assembly, in the
        // order they were in, and drop the rest. ConfigureWhitelist expands an anchor to
        // its whole assembly, so the first one already covers everything the others would
        // have — but only removals are safe, because anything added here ends up inside a
        // mod's compile whitelist.
        Assert.Equal(before.DistinctBy(anchor => anchor.Assembly), after);

        Assert.True(after.Length < before.Length,
            "nothing was dropped, so this proves nothing — the stub should be seeding a duplicate");
        Assert.Distinct(after.Select(anchor => anchor.Assembly));
    }

    [PluginBuiltFact]
    public void Without_the_launch_option_it_turns_scripting_on()
    {
        var harness = new PulsarHarness().Instantiate();
        var engine = EngineWithProjectManager(out var projectManager);

        Assert.Empty(projectManager.CodeProviders);   // the state the flag would have changed

        harness.RaiseEngineEvent(engine);

        // AddScripting registers four providers under four fixed ProjectType keys. Getting
        // all four is the difference between "scripting is on" and "mods compile but DLC
        // projects do not".
        Assert.Equal(
            new[] { ProjectType.Unknown, ProjectType.Vanilla, ProjectType.DLC, ProjectType.Mod }.Order(),
            projectManager.CodeProviders.Keys.Order());

        AssertDedupedFrom(
            GameApp.Anchors(),
            GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies);
        AssertDedupedFrom(
            GameApp.Anchors(),
            GameWhitelistProvider<InGameWhitelistProvider>.AllowedAssemblies);

        Assert.Contains("script mods are enabled", harness.Lines);
    }

    [PluginBuiltFact]
    public void With_the_launch_option_it_leaves_scripting_alone_and_still_fixes_the_whitelist()
    {
        var harness = new PulsarHarness().Instantiate();
        var engine = EngineWithProjectManager(out var projectManager);

        // GameApp.cs:322 — the -loadScripts branch, which runs before the event at :334.
        GameApp.AddScripting(engine);
        var registered = projectManager.CodeProviders.Count;

        harness.RaiseEngineEvent(engine);

        // CodeProviders.Add on four fixed keys means a second AddScripting throws
        // ArgumentException out of the game's own startup. The empty-dictionary check in
        // EnsureScripting is the whole defence, and this is what it is defending.
        Assert.Equal(registered, projectManager.CodeProviders.Count);
        Assert.Contains("scripting already registered", harness.Lines);

        // The duplicate anchor is the game's, not ours — so it is there with the launch
        // option too, and this is the configuration where a player hits it today.
        AssertDedupedFrom(
            GameApp.Anchors(),
            GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies);
        Assert.Contains("script mods are enabled", harness.Lines);
    }

    [PluginBuiltFact]
    public void Running_twice_changes_nothing_and_throws_nothing()
    {
        var harness = new PulsarHarness().Instantiate();
        var engine = EngineWithProjectManager(out var projectManager);

        harness.RaiseEngineEvent(engine);
        var afterFirst = GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies;

        harness.RaiseEngineEvent(engine);

        // Not a configuration the game produces, but the plugin has no way to know that,
        // and both edits have to be safe to repeat for either of them to be safe at all.
        Assert.Equal(4, projectManager.CodeProviders.Count);
        Assert.Equal(afterFirst, GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies);
        Assert.Contains("no duplicates", harness.Lines);
    }

    [PluginBuiltFact]
    public void An_engine_it_does_not_recognise_is_left_exactly_as_it_was()
    {
        var harness = new PulsarHarness().Instantiate();

        // No ProjectManager object builder anywhere. The game's own OB<T> throws on this;
        // the plugin declines, because the game is entitled to assume its own component
        // list and a plugin reading it mid-construction is not.
        var engine = new EngineBuilder();
        engine.EntityBuilder.Components = [new EntityBuilder.ComponentBuildInfo { ObjectBuilder = new object() }];

        harness.RaiseEngineEvent(engine);

        Assert.Contains("not registering scripting", harness.Lines);
        Assert.DoesNotContain("script mods are enabled", harness.Lines);
    }

    [PluginBuiltFact]
    public void A_component_list_that_is_not_there_yet_is_not_a_crash()
    {
        var harness = new PulsarHarness().Instantiate();

        // Components is null until the engine starts composing. Dereferencing it would be
        // a NullReferenceException raised from inside PluginHost's invoke, which nothing
        // above catches.
        harness.RaiseEngineEvent(new EngineBuilder());

        Assert.Contains("not registering scripting", harness.Lines);
    }

    [PluginBuiltFact]
    public void A_failure_halfway_through_stays_inside_the_plugin()
    {
        var harness = new PulsarHarness().Instantiate();
        var engine = EngineWithProjectManager(out _);

        GameApp.AddScripting(engine);   // so the run gets past EnsureScripting

        // A whitelist someone else has already made nonsense of. Any exception will do —
        // the point is that the handler is the last frame that can catch one, and the cost
        // of it escaping is a game that will not start.
        GameWhitelistProvider<ModWhitelistProvider>.AllowedAssemblies = [null!];

        var escaped = Record.Exception(() => harness.RaiseEngineEvent(engine));

        Assert.Null(escaped);
        Assert.Contains("failed, leaving the game as it was", harness.Lines);
    }
}
