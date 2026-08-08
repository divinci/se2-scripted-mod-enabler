using System;

namespace SE2ScriptedModEnabler;

/// <summary>
/// The SE2 builds this plugin has been tested against.
///
/// Fail-closed by construction: an unlisted build leaves the plugin inert. There is
/// deliberately no runtime way to widen this list — no config file, no environment
/// variable, no command-line flag. "Is this build safe?" must not be answerable by
/// anything that can write a file next to the DLL, because the cost of getting it
/// wrong is a game that will not launch.
///
/// Mirrored into [AssemblyMetadata("SupportedBuilds", ...)] by the csproj so the
/// installer can read it without loading this assembly. KnownBuildsTests asserts
/// the two stay in sync.
/// </summary>
internal static class KnownBuilds
{
    internal const string MetadataKey = "SupportedBuilds";

    /// <summary>Four-part AssemblyFileVersion of SpaceEngineers2.dll.</summary>
    internal static readonly string[] Stamps = ["2.3.0.2798"];

    internal static bool Allows(string? stamp) =>
        !string.IsNullOrEmpty(stamp) && Array.IndexOf(Stamps, stamp) >= 0;

    internal static string Describe() => string.Join(", ", Stamps);
}
