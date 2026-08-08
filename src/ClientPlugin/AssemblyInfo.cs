#nullable enable
// Pulsar compiles with nullable off, so every file turns it on explicitly.

using System;

// GameApp.AddScripting is private. Pulsar's compiler scans plugin sources for this
// attribute (Compiler/PublicizedAssemblies.InspectSource) and, when it finds one, rewrites
// that reference with every member made public before Roslyn sees it
// (Compiler/Publicizer.PublicizeReference) — mixed-mode assemblies included, which
// SpaceEngineers2.dll is.
//
// The name has to match the key Pulsar files references under, which is
// Path.GetFileNameWithoutExtension of the dll (Shared/Tools.GetFiles). So: no path, no
// extension, no version.
//
// Nothing in a stub build needs this — the stub declares AddScripting public — so the only
// thing that catches its removal is DeclaresThePublicizerAttribute in PluginSurfaceTests.
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("SpaceEngineers2")]

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Not in the BCL — the runtime, Roslyn and Pulsar all recognise it by full name only,
    /// so an assembly that wants one declares it itself.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    {
        public string AssemblyName { get; } = assemblyName;
    }
}
