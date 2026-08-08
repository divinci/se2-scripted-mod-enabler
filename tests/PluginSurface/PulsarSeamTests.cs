using System.Reflection;
using System.Runtime.Loader;
using Keen.VRage.Core.EngineComponents;
using Keen.VRage.Core.Plugins;
using GameLog = Keen.VRage.Library.Diagnostics.Log;

namespace SE2ScriptedModEnabler.Tests;

/// <summary>
/// Stands in for <c>NLog.LogLevel</c>, which is what Pulsar's real sink takes as its second
/// parameter. A class, not an enum: the plugin passes null for it, and that only binds
/// because the parameter is a reference type.
/// </summary>
internal sealed class FakeLogLevel
{
}

/// <summary>
/// Statics the game stubs own — <c>GameLog.Default</c> and both whitelists — live in the
/// default load context and so are shared by every test in the process. xunit runs classes
/// in parallel unless they share a collection, so the ones that write to them say so here.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GameStaticsCollection
{
    public const string Name = "game statics";
}

/// <summary>
/// Pulsar's loader, reproduced closely enough to prove the seam binds, with no Pulsar and
/// no game. Mirrors <c>Modern/Loader/PluginInstance.cs</c> at Pulsar 07e8774: the main type
/// is found the way <c>TryGet</c> finds it, and the field is looked up with Harmony's
/// declared-only binding flags.
///
/// <para>Each harness loads the plugin into its own AssemblyLoadContext, because the
/// plugin's diagnostics are static and a shared load would leak one test's lines into the
/// next. The game stubs still resolve through the default context, so the types this file
/// names are the same types the plugin binds to.</para>
/// </summary>
internal sealed class PulsarHarness
{
    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;

    public List<(string Text, object? Level)> Log { get; } = [];
    public int SinkCalls { get; private set; }

    public Assembly Assembly { get; }
    public Type MainType { get; }
    public PluginHost Host { get; } = new PluginHost(["Game2.exe"]);
    public object? Plugin { get; private set; }
    public Exception? InstantiateError { get; private set; }

    public PulsarHarness(bool sinkThrows = false)
    {
        var context = new AssemblyLoadContext($"pulsar-harness-{Guid.NewGuid():N}", isCollectible: false);
        Assembly = context.LoadFromAssemblyPath(Path.GetFullPath(PluginAssembly.Path!));

        // PluginInstance.TryGet, verbatim in behaviour: first type assignable to IPlugin,
        // with no filter for abstract and no naming convention.
        MainType = Assembly.GetTypes().First(t => typeof(IPlugin).IsAssignableFrom(t));

        Action<string, FakeLogLevel> sink = (text, level) =>
        {
            SinkCalls++;
            if (sinkThrows) throw new InvalidOperationException("the log is on fire");
            Log.Add((text, level));
        };

        MainType.GetField("PulsarLog", Declared)?.SetValue(null, sink);
    }

    /// <summary>
    /// <c>PluginInstance.Instantiate</c>. Pulsar catches here, so a throwing constructor is
    /// recorded rather than rethrown — and these tests have to tell the difference between
    /// "the plugin caught its own problem" and "Pulsar caught it".
    /// </summary>
    public PulsarHarness Instantiate()
    {
        try
        {
            Plugin = Activator.CreateInstance(MainType, Host);
        }
        catch (Exception ex)
        {
            InstantiateError = ex;
        }

        return this;
    }

    /// <summary>
    /// The game's second call into the plugin, raised the way <c>GameApp</c> raises it:
    /// straight through <c>PluginHost</c>, with nothing catching on the way out.
    /// </summary>
    public void RaiseEngineEvent(EngineBuilder engine) => Host.RaiseOnBeforeEngineInstantiated(engine);

    public string Lines => string.Join("\n", Log.Select(entry => entry.Text));

    public void Flush() =>
        Assembly.GetType("SE2ScriptedModEnabler.Log")!
            .GetMethod("Flush", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);
}

/// <summary>
/// Pulsar's dependency injection, driven for real against the built plugin.
///
/// <para>PluginSurfaceTests checks the field has the right shape in metadata. This checks
/// that shape is one Pulsar can actually assign to and call — not the same claim, and the
/// interesting failure (an <c>object</c> field that turns out not to accept the delegate, a
/// <c>DynamicInvoke</c> that will not bind a null level) only shows up when something does
/// the assigning.</para>
/// </summary>
[Collection(GameStaticsCollection.Name)]
public class PulsarSeamTests
{
    [PluginBuiltFact]
    public void The_plugin_constructs_under_pulsars_loader()
    {
        var harness = new PulsarHarness().Instantiate();

        Assert.Null(harness.InstantiateError);
        Assert.NotNull(harness.Plugin);
    }

    [PluginBuiltFact]
    public void Diagnostics_reach_the_injected_log()
    {
        var harness = new PulsarHarness().Instantiate();

        Assert.NotEmpty(harness.Log);
        Assert.All(harness.Log, entry => Assert.StartsWith("[SE2SME] ", entry.Text));

        // Pulsar's LogFile.WriteLine substitutes Info for a null level; passing null is how
        // the plugin avoids naming NLog.LogLevel.
        Assert.All(harness.Log, entry => Assert.Null(entry.Level));
    }

    [PluginBuiltFact]
    public void The_version_a_bug_report_will_quote_is_the_first_thing_it_says()
    {
        var harness = new PulsarHarness().Instantiate();

        // Roslyn-compiled by Pulsar means no informational version and no assembly version
        // worth reading, so this line is the only version anyone can see.
        Assert.Matches(@"^\[SE2SME\] v\d+\.\d+\.\d+ loaded$", harness.Log[0].Text);
    }

    [PluginBuiltFact]
    public void A_broken_log_sink_is_dropped_rather_than_retried()
    {
        var harness = new PulsarHarness(sinkThrows: true).Instantiate();

        Assert.Null(harness.InstantiateError);

        // One attempt, then the channel is written off. The alternative is a throwing call
        // on every diagnostic line for the rest of the session.
        Assert.Equal(1, harness.SinkCalls);
    }

    /// <summary>
    /// The other half of the plugin's logging, which the Pulsar sink tests cannot see: lines
    /// written while <c>GameLog.Default</c> is still null have to survive until it is not.
    /// </summary>
    [PluginBuiltFact]
    public void Lines_buffered_before_the_game_log_exists_arrive_once_it_is_up()
    {
        var previous = GameLog.Default;

        try
        {
            // The state the constructor really runs in, and the reason the buffer exists.
            GameLog.Default = null;
            var harness = new PulsarHarness().Instantiate();

            Assert.Null(harness.InstantiateError);
            Assert.NotEmpty(harness.Log);   // something was written, so there is a drain to test

            var gameLog = new GameLog();
            GameLog.Default = gameLog;
            harness.Flush();

            // Both channels get the identical string, which is only true because the tag is
            // applied once, before either sink sees the line.
            Assert.Equal(harness.Log.Select(entry => entry.Text), gameLog.Lines);

            // The buffer emptied rather than being replayed: a drain that wrote everything
            // must leave nothing behind to write a second time.
            harness.Flush();
            Assert.Equal(harness.Log.Count, gameLog.Lines.Count);
        }
        finally
        {
            GameLog.Default = previous;
        }
    }

    [PluginBuiltFact]
    public void Only_one_type_answers_the_search_pulsar_uses_to_pick_a_main_type()
    {
        var harness = new PulsarHarness();

        var candidates = harness.Assembly.GetTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .ToArray();

        // TryGet takes FirstOrDefault with no ordering guarantee and no abstract filter, so
        // "there is only one" is the only version of this that is safe to rely on.
        Assert.Equal([PluginAssembly.PluginTypeName], candidates);
    }
}
