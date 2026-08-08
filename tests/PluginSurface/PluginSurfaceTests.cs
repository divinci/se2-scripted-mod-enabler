using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SE2ScriptedModEnabler.Tests;

/// <summary>
/// Skips only when there is nothing to read, which since the GameStubs projects landed
/// should never happen — the test project builds the plugin as a side effect. The workflow
/// asserts on the skipped count, because a skip here looks exactly like a pass.
/// </summary>
public sealed class PluginBuiltFactAttribute : FactAttribute
{
    public PluginBuiltFactAttribute()
    {
        if (PluginAssembly.Path is null)
            Skip = "no plugin dll found — build src/ClientPlugin, or point SME_PLUGIN_DLL at one";
    }
}

internal static class PluginAssembly
{
    /// <summary>
    /// Point this at a build made against the real game assemblies — the only guard
    /// against the stubs drifting. Taken verbatim: if it names a file that is not there
    /// the tests fail rather than quietly skip.
    /// </summary>
    private const string Override = "SME_PLUGIN_DLL";

    private const string Dll = "SE2ScriptedModEnabler.dll";

    public static readonly string? Path = Find();

    private static string? Find()
    {
        var chosen = Environment.GetEnvironmentVariable(Override);
        if (!string.IsNullOrWhiteSpace(chosen)) return chosen;

        // Build the same configuration this test assembly was built in first, so a stale
        // copy of the other one cannot win. Stub before real: the stub is rebuilt by this
        // project's ProjectReference and so is always current.
        var mine = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var configs = mine == "Release" ? new[] { "Release", "Debug" } : ["Debug", "Release"];

        var candidates =
            from tree in new[] { "stub", "real" }
            from config in configs
            select System.IO.Path.Combine(Repo.Root, "src", "ClientPlugin", "bin", tree, config, "net9.0", Dll);

        return candidates.FirstOrDefault(File.Exists);
    }

    public static AssemblyDefinition Read() => AssemblyDefinition.ReadAssembly(Path!);

    public static TypeDefinition PluginType(AssemblyDefinition assembly) =>
        AllTypes(assembly.MainModule).Single(t => t.FullName == PluginTypeName);

    public const string PluginTypeName = "SE2ScriptedModEnabler.SE2ScriptedModEnablerPlugin";

    /// <summary>
    /// Every game assembly this plugin binds to, and so every one Keen could break it with.
    ///
    /// <para>Recompiling against the installed game is Pulsar's answer to a game update, and
    /// it only answers for what the compiler can see. Growing this list is not forbidden —
    /// it is the point of binding early — but it is the moment to ask whether the new member
    /// is one Keen is likely to move, because a rename anywhere in here takes the plugin out
    /// of service until a release catches up.</para>
    /// </summary>
    public static readonly string[] GameReferences =
    [
        "Game2.Simulation",     // GameWhitelistProvider<T>, ModWhitelistProvider, InGameWhitelistProvider
        "SpaceEngineers2",      // GameApp.AddScripting
        "VRage.Core",           // IPlugin, PluginHost, EngineBuilder, ProjectManagerEngineComponent
        "VRage.DCS",            // EntityBuilder, EntityBuilder.ComponentBuildInfo
        "VRage.Library",        // Log, Singleton<T>
    ];

    public static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in type.NestedTypes) yield return nested;
        }
    }

    public static bool IsKeen(TypeReference? type)
    {
        while (type is not null)
        {
            if (type.Namespace.StartsWith("Keen", StringComparison.Ordinal)) return true;
            if (type is GenericInstanceType generic && generic.GenericArguments.Any(IsKeen)) return true;
            type = type is TypeSpecification spec ? spec.ElementType : null;
        }
        return false;
    }
}

/// <summary>
/// What the built assembly promises about itself, checked against the metadata rather than
/// trusted.
///
/// <para>The plugin binds to the game at compile time, which is what lets Pulsar's
/// rebuild-per-update act as the version check. That trade only holds if the compile Pulsar
/// runs is the compile that happened here — same references, same publicized assembly, and
/// nothing pulled in that only exists under one of the two loaders.</para>
/// </summary>
public class PluginSurfaceTests
{
    [PluginBuiltFact]
    public void References_exactly_the_game_assemblies_it_binds_to()
    {
        using var assembly = PluginAssembly.Read();

        var keenish = assembly.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => name.StartsWith("VRage", StringComparison.Ordinal)
                        || name.StartsWith("Game2", StringComparison.Ordinal)
                        || name.StartsWith("SpaceEngineers", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Pulsar hands Roslyn a superset of this (SpaceEngineers2.dll, VRage*.dll,
        // Game2*.dll — Modern/References.cs), so nothing that compiles here can fail there
        // for want of a reference. The list is asserted rather than bounded because it is
        // the plugin's whole exposure to a game update, and it should take a deliberate
        // edit to widen it.
        Assert.Equal(PluginAssembly.GameReferences, keenish);
    }

    [PluginBuiltFact]
    public void References_nothing_pulsar_supplies()
    {
        using var assembly = PluginAssembly.Read();

        var borrowed = assembly.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => name is "NLog" or "0Harmony"
                        || name.StartsWith("Pulsar", StringComparison.Ordinal)
                        || name.StartsWith("Avalonia", StringComparison.Ordinal))
            .ToArray();

        // Pulsar's own assemblies are not in the reference set it compiles against
        // (Modern/References.cs lists the game plus a fixed base environment), so naming
        // one is a compile error on the player's machine and nowhere else. It is also why
        // PulsarLog is an object field rather than an Action<string, LogLevel>.
        Assert.Empty(borrowed);
    }

    [PluginBuiltFact]
    public void Exactly_one_type_inherits_from_the_game()
    {
        using var assembly = PluginAssembly.Read();

        var derived = PluginAssembly.AllTypes(assembly.MainModule)
            .Where(type => PluginAssembly.IsKeen(type.BaseType) || type.Interfaces.Any(i => PluginAssembly.IsKeen(i.InterfaceType)))
            .Select(type => type.FullName)
            .ToArray();

        // Pulsar picks our main type with FirstOrDefault over assembly.GetTypes()
        // (PluginInstance.TryGet), which does not filter abstract types and promises no
        // metadata order — a second implementer could quietly become the one it injects
        // PulsarLog into, and the one it constructs.
        Assert.Equal([PluginAssembly.PluginTypeName], derived);
    }

    /// <summary>
    /// Pulsar's FirstChanceException handler (PluginLoader.OnException) unwraps one level
    /// of InnerException, tests <c>TargetSite.DeclaringType.Assembly == mainAssembly</c>,
    /// and on a match disables the plugin <em>and</em> deletes its compiled cache — before
    /// any catch of ours runs. Throwing one of these is self-destruct, not error handling.
    /// </summary>
    private static readonly string[] SelfDestructExceptions =
    [
        "System.MemberAccessException",
        "System.FieldAccessException",
        "System.MethodAccessException",
        "System.MissingMemberException",
        "System.MissingFieldException",
        "System.MissingMethodException",
        "System.TypeAccessException",
    ];

    [PluginBuiltFact]
    public void Nothing_constructs_a_MemberAccessException()
    {
        using var assembly = PluginAssembly.Read();

        var offenders = new List<string>();

        foreach (var type in PluginAssembly.AllTypes(assembly.MainModule))
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Newobj) continue;
                if (instruction.Operand is not MethodReference target) continue;
                if (!SelfDestructExceptions.Contains(target.DeclaringType.FullName)) continue;

                offenders.Add($"{type.FullName}::{method.Name} constructs {target.DeclaringType.FullName}");
            }
        }

        Assert.Empty(offenders);
    }

    [PluginBuiltFact]
    public void The_engine_event_handler_catches_everything()
    {
        using var assembly = PluginAssembly.Read();

        var handler = PluginAssembly.PluginType(assembly).Methods
            .SingleOrDefault(m => m.Name == "OnBeforeEngineInstantiated");

        Assert.NotNull(handler);

        var body = handler!.Body;

        // Keen raises this from PluginHost.InvokeOnBeforeEngineInstantiated with no
        // try/catch, and Pulsar cannot add one: it wraps the constructor only, and its
        // FirstChanceException handler looks for MemberAccessException alone. Anything that
        // escapes here takes the game's startup with it, on every machine that has the
        // plugin installed.
        var catchAll = body.ExceptionHandlers.SingleOrDefault(h =>
            h.HandlerType == ExceptionHandlerType.Catch
            && h.CatchType?.FullName == "System.Exception");

        Assert.True(catchAll is not null,
            "OnBeforeEngineInstantiated must have exactly one catch (Exception) — "
            + $"found {body.ExceptionHandlers.Count} handler(s), none or more than one of them that.");

        // And it has to cover the whole method, not a promising-looking part of it. Asked
        // as "nothing is called before the try begins" rather than "the try begins at
        // instruction zero", because a Debug build puts a nop there.
        var unprotected = body.Instructions
            .TakeWhile(i => i != catchAll!.TryStart)
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
            .Select(i => ((MethodReference)i.Operand).FullName)
            .ToArray();

        Assert.Empty(unprotected);
    }
}

/// <summary>
/// The publicizer seam, which is the one thing in this repository that a green build says
/// nothing about.
///
/// <para><c>GameApp</c> is internal and <c>AddScripting</c> is private. Pulsar reaches them
/// by scanning the plugin's <em>source</em> for an <c>IgnoresAccessChecksTo</c> attribute
/// and, on a hit, rewriting that reference with everything public before Roslyn sees it
/// (Compiler/PublicizedAssemblies.cs, Compiler/Publicizer.cs). The stub build cannot
/// reproduce that — it declares the end state instead — so deleting AssemblyInfo.cs would
/// leave every other test in this file green and break the plugin for every player.</para>
/// </summary>
public class PublicizerSeamTests
{
    /// <summary>
    /// <c>PublicizedAssemblies.InspectSource</c>, run on the files Pulsar would compile.
    /// Same parser, same predicate — the interesting failures are the ones a text search
    /// would miss, like an attribute argument that stopped being a literal.
    /// </summary>
    private static string[] AssembliesPulsarWouldPublicize()
    {
        var found = new List<string>();

        foreach (var file in Repo.AllSources().Where(f => f.StartsWith(Repo.PluginDir + "/", StringComparison.Ordinal)))
        {
            var source = SourceText.From(File.ReadAllText(Path.Combine(Repo.Root, file)));
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();

            var attributes = root.DescendantNodes()
                .OfType<AttributeSyntax>()
                .Where(attr => attr.Name.ToString().EndsWith("IgnoresAccessChecksTo"));

            foreach (var attribute in attributes)
            {
                var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
                if (argument?.Expression is not LiteralExpressionSyntax literal) continue;
                if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) continue;

                found.Add(literal.Token.ValueText);
            }
        }

        return found.Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void Pulsars_source_scan_finds_the_one_reference_we_need_publicized()
    {
        // Exactly one, and that one. Publicizing more would be free at compile time and
        // would quietly widen what a future edit could reach into without anyone deciding
        // to; publicizing none is a build failure on the player's machine only.
        Assert.Equal(["SpaceEngineers2"], AssembliesPulsarWouldPublicize());
    }

    [Fact]
    public void The_name_is_the_key_pulsar_files_references_under()
    {
        var name = Assert.Single(AssembliesPulsarWouldPublicize());

        // PublicizeReferenceIfRequired matches against the dictionary key Pulsar builds
        // with Path.GetFileNameWithoutExtension (Shared/Tools.GetFiles). A path, a .dll or
        // a version would simply never match, and Pulsar says nothing when it does not —
        // the first sign would be a compile error about accessibility.
        Assert.Equal(Path.GetFileNameWithoutExtension(name), name);
        Assert.DoesNotContain(",", name);   // not a full assembly name either
    }

    [PluginBuiltFact]
    public void The_publicized_assembly_is_one_the_plugin_actually_references()
    {
        var name = Assert.Single(AssembliesPulsarWouldPublicize());

        // Publicizing costs a full Cecil rewrite of the assembly on every cache miss, and
        // buys nothing for a reference nothing binds to.
        Assert.Contains(name, PluginAssembly.GameReferences);
    }

    [PluginBuiltFact]
    public void The_attribute_survives_into_the_assembly_so_the_runtime_honours_it()
    {
        using var assembly = PluginAssembly.Read();

        var applied = assembly.CustomAttributes
            .Where(a => a.AttributeType.Name == "IgnoresAccessChecksToAttribute")
            .Select(a => (string)a.ConstructorArguments[0].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Publicizing only settles the compile. The IL that comes out still calls a private
        // method on an internal type, and the runtime rejects that unless this attribute is
        // in the emitted assembly — which means the declaration has to be compiled in too,
        // because it is not in the BCL.
        Assert.Equal(AssembliesPulsarWouldPublicize(), applied);
    }
}

/// <summary>
/// The shape of Pulsar's injection seam, as metadata.
///
/// <para><c>PluginInstance.DependencyInject</c> looks the field up with
/// <c>AccessTools.DeclaredField</c> on the main type only, by exact name, in its own
/// try/catch — so getting it wrong costs a line in Pulsar's log and nothing else. That
/// silence is the problem: renaming it would send every diagnostic into a void with no
/// test failing. PulsarSeamTests then proves it actually binds.</para>
/// </summary>
public class PulsarSeamShapeTests
{
    [PluginBuiltFact]
    public void PulsarLog_is_a_static_object_field()
    {
        using var assembly = PluginAssembly.Read();

        var field = PluginAssembly.PluginType(assembly).Fields.SingleOrDefault(f => f.Name == "PulsarLog");

        Assert.NotNull(field);
        Assert.True(field!.IsStatic, "DeclaredField(...).SetValue(null, ...) — it has to be static");

        // Pulsar assigns an Action<string, NLog.LogLevel>. Declaring that type here would
        // put NLog in our metadata, and NLog is not in the reference set Pulsar compiles
        // against; SetValue only needs assignability.
        Assert.Equal("System.Object", field.FieldType.FullName);
    }

    [PluginBuiltFact]
    public void The_injected_field_is_never_read_from_a_static_initializer()
    {
        using var assembly = PluginAssembly.Read();

        var cctor = PluginAssembly.PluginType(assembly).Methods.SingleOrDefault(m => m.Name == ".cctor");
        if (cctor is null) return;   // no static initializer at all is the best outcome

        var reads = cctor.Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldsfld)
            .Select(i => ((FieldReference)i.Operand).Name)
            .Where(name => name is "PulsarLog")
            .ToArray();

        // SetValue on a static field runs the class constructor first, so a cctor that
        // touched this would observe it still null — and cache that, permanently, before
        // Pulsar has assigned anything.
        Assert.Empty(reads);
    }
}
