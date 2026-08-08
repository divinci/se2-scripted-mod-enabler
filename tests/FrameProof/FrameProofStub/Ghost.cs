namespace FrameProofStub;

/// <summary>
/// Stands in for a game type the plugin binds at compile time. Exists at build, gone at
/// run — which is what a Keen rename looks like from the JIT's point of view.
/// </summary>
public static class Ghost
{
    public static string Speak() => "the stub is still here";
}
