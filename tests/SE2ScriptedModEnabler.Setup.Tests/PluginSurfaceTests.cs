using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SE2ScriptedModEnabler.Setup.Tests;

/// <summary>
/// Skips itself when the plugin has not been built. The plugin needs the game's
/// assemblies to compile, so CI on a machine without Space Engineers 2 cannot produce
/// it — but when it is there, these tests must run.
/// </summary>
public sealed class PluginBuiltFactAttribute : FactAttribute
{
    public PluginBuiltFactAttribute()
    {
        if (PluginAssembly.Path is null)
            Skip = "the plugin is not built — run tools/build-plugin.sh (needs the game installed)";
    }
}

internal static class PluginAssembly
{
    public static readonly string? Path = Find();

    private static string? Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "se2sme.sln")))
            dir = dir.Parent;
        if (dir is null) return null;

        var candidates = new[] { "Release", "Debug" }
            .Select(config => System.IO.Path.Combine(
                dir.FullName, "src", "SE2ScriptedModEnabler", "bin", config, "net9.0", Files.PluginDll));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static AssemblyDefinition Read() => AssemblyDefinition.ReadAssembly(Path!);

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
/// The safety claims in Plugin.cs, checked against the IL rather than trusted.
///
/// <para>PluginHost calls <c>Activator.CreateInstance</c> on our constructor and invokes
/// <c>OnBeforeEngineInstantiated</c> with no try/catch around either. Anything we throw
/// out of those two takes the game's startup with it — on every machine that has this
/// installed, the first time Keen ships a build we have not seen. These tests exist
/// because the discipline that prevents that is invisible: it lives in which method a
/// type name appears in, and nothing about ordinary C# makes moving a line between two
/// methods look dangerous.</para>
/// </summary>
public class PluginSurfaceTests
{
    /// <summary>
    /// The plugin ABI, and the whole of it. PluginHost's own <c>assembly.GetTypes()</c>
    /// is unguarded, so if these move the game is already broken before we get a say —
    /// which is exactly why nothing else may join them.
    /// </summary>
    private static readonly string[] AllowedKeenTypes =
    [
        "Keen.VRage.Core.Plugins.IPlugin",
        "Keen.VRage.Core.Plugins.PluginHost",
        "Keen.VRage.Core.EngineComponents.EngineBuilder",
        "Keen.VRage.Library.Diagnostics.Log",
    ];

    /// <summary>
    /// Entry points, which by definition have nothing of ours one frame up to catch for
    /// them. Both are called directly by PluginHost and both name a game type in their
    /// signature, so neither can be protected — they must instead stay trivial: a
    /// try/catch around a single NoInlining call and nothing else.
    /// </summary>
    private static readonly string[] AbiEntryPoints =
    [
        "SE2ScriptedModEnabler.SE2ScriptedModEnablerPlugin::.ctor",
        "SE2ScriptedModEnabler.SE2ScriptedModEnablerPlugin::OnBeforeEngineInstantiated",
    ];

    [PluginBuiltFact]
    public void References_no_game_assembly_beyond_the_two_the_abi_lives_in()
    {
        using var assembly = PluginAssembly.Read();

        var keenish = assembly.MainModule.AssemblyReferences
            .Select(reference => reference.Name)
            .Where(name => name.StartsWith("VRage", StringComparison.Ordinal)
                        || name.StartsWith("Game2", StringComparison.Ordinal)
                        || name.StartsWith("SpaceEngineers", StringComparison.Ordinal))
            .OrderBy(name => name)
            .ToArray();

        // Game2.Simulation, Game2.Client and SpaceEngineers2 are reached only through
        // GameBridge, by string. That is what makes a rename inert instead of fatal.
        Assert.Equal(["VRage.Core", "VRage.Library"], keenish);
    }

    [PluginBuiltFact]
    public void Names_no_game_type_beyond_the_abi()
    {
        using var assembly = PluginAssembly.Read();

        var referenced = assembly.MainModule.GetTypeReferences()
            .Where(PluginAssembly.IsKeen)
            .Select(type => type.FullName)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(AllowedKeenTypes.OrderBy(name => name, StringComparer.Ordinal), referenced);
    }

    [PluginBuiltFact]
    public void Exactly_one_type_inherits_from_the_game()
    {
        using var assembly = PluginAssembly.Read();

        var derived = assembly.MainModule.Types
            .Concat(assembly.MainModule.Types.SelectMany(t => t.NestedTypes))
            .Where(type => PluginAssembly.IsKeen(type.BaseType) || type.Interfaces.Any(i => PluginAssembly.IsKeen(i.InterfaceType)))
            .Select(type => type.FullName)
            .ToArray();

        // GetTypes() resolves every type's base and interfaces in one go, and PluginHost
        // does not guard that call. One type's worth of exposure is unavoidable; two is
        // a choice.
        Assert.Equal(["SE2ScriptedModEnabler.SE2ScriptedModEnablerPlugin"], derived);
    }

    [PluginBuiltFact]
    public void Every_method_that_touches_a_game_type_is_noinlining()
    {
        using var assembly = PluginAssembly.Read();

        var offenders = new List<string>();

        foreach (var type in AllTypes(assembly.MainModule))
        foreach (var method in type.Methods)
        {
            if (!TouchesKeen(method)) continue;

            var name = $"{type.FullName}::{method.Name}";
            if (AbiEntryPoints.Contains(name)) continue;
            if (method.NoInlining) continue;

            offenders.Add(name);
        }

        // Without NoInlining the JIT may fold the method into its caller, which moves
        // the type resolution up into the frame that was supposed to be catching for it
        // — turning a caught TypeLoadException back into a game that will not start.
        Assert.Empty(offenders);
    }

    [PluginBuiltFact]
    public void The_abi_entry_points_stay_trivial()
    {
        using var assembly = PluginAssembly.Read();

        foreach (var name in AbiEntryPoints)
        {
            var method = Method(assembly, name);
            Assert.NotNull(method);

            Assert.True(method!.Body.ExceptionHandlers.Count >= 1,
                $"{name} must wrap its work in try/catch — nothing above it will.");

            var calls = method.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                .Select(i => ((MethodReference)i.Operand))
                .Where(target => target.DeclaringType.Scope == assembly.MainModule)
                .Select(target => target.Resolve())
                .Where(target => target is not null)
                .ToList();

            Assert.All(calls, target => Assert.True(target!.NoInlining || target.Name == ".ctor",
                $"{name} calls {target!.Name}, which must be NoInlining to keep the frames apart."));
        }
    }

    [PluginBuiltFact]
    public void The_supported_build_list_matches_its_metadata_mirror()
    {
        using var assembly = PluginAssembly.Read();

        var metadata = assembly.CustomAttributes
            .Where(a => a.AttributeType.Name == "AssemblyMetadataAttribute")
            .Where(a => (string?)a.ConstructorArguments[0].Value == "SupportedBuilds")
            .Select(a => (string?)a.ConstructorArguments[1].Value)
            .SingleOrDefault();

        Assert.False(string.IsNullOrWhiteSpace(metadata),
            "the csproj must set AssemblyMetadata SupportedBuilds — the installer reads it "
            + "without loading an assembly that binds to the game");

        var knownBuilds = AllTypes(assembly.MainModule).Single(t => t.Name == "KnownBuilds");
        var compiledIn = knownBuilds.Methods.Single(m => m.Name == ".cctor")
            .Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr)
            .Select(i => (string)i.Operand)
            .ToHashSet();

        var mirrored = metadata!.Split(',').Select(s => s.Trim()).ToHashSet();

        // Two copies of the allowlist, and the installer trusts the one it can read
        // without loading game-bound code. They have to agree.
        Assert.Equal(mirrored, compiledIn);
    }

    [PluginBuiltFact]
    public void The_supported_builds_look_like_four_part_versions()
    {
        using var assembly = PluginAssembly.Read();

        var stamps = AllTypes(assembly.MainModule).Single(t => t.Name == "KnownBuilds")
            .Methods.Single(m => m.Name == ".cctor")
            .Body.Instructions
            .Where(i => i.OpCode == OpCodes.Ldstr)
            .Select(i => (string)i.Operand)
            .ToArray();

        Assert.NotEmpty(stamps);

        // VersionExt.Version packs Major*1e6 + Minor*1e3 + Build and drops the
        // revision, which is the only part that moves between builds. Gating on three
        // parts would arm the plugin on every future 2.3.0.x.
        Assert.All(stamps, stamp => Assert.Equal(4, stamp.Split('.').Length));
    }

    private static bool TouchesKeen(MethodDefinition method)
    {
        if (PluginAssembly.IsKeen(method.ReturnType)) return true;
        if (method.Parameters.Any(p => PluginAssembly.IsKeen(p.ParameterType))) return true;
        if (!method.HasBody) return false;
        if (method.Body.Variables.Any(v => PluginAssembly.IsKeen(v.VariableType))) return true;

        foreach (var instruction in method.Body.Instructions)
        {
            var touched = instruction.Operand switch
            {
                TypeReference type => PluginAssembly.IsKeen(type),
                MethodReference target => PluginAssembly.IsKeen(target.DeclaringType)
                                          || PluginAssembly.IsKeen(target.ReturnType)
                                          || target.Parameters.Any(p => PluginAssembly.IsKeen(p.ParameterType)),
                FieldReference field => PluginAssembly.IsKeen(field.DeclaringType) || PluginAssembly.IsKeen(field.FieldType),
                _ => false,
            };
            if (touched) return true;
        }

        return false;
    }

    private static MethodDefinition? Method(AssemblyDefinition assembly, string qualified)
    {
        var split = qualified.Split("::");
        return AllTypes(assembly.MainModule).SingleOrDefault(t => t.FullName == split[0])
            ?.Methods.SingleOrDefault(m => m.Name == split[1]);
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            yield return type;
            foreach (var nested in type.NestedTypes) yield return nested;
        }
    }
}
