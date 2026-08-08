using System.Text;

namespace SE2ScriptedModEnabler.Setup.Tests;

internal static class Fixture
{
    /// <summary>The real shipping file, byte for byte, copied out of the game folder.</summary>
    public const string RuntimeConfigSha =
        "959689aed61a7564d83a15f1fc7750bdba8762e5a10600008315ec18c2a9859c";

    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));

    public static string Text(string name) => File.ReadAllText(Path(name));

    /// <summary>The shipping runtimeconfig.json: CRLF, no BOM, no trailing newline.</summary>
    public static byte[] RuntimeConfig() => Bytes("SpaceEngineers2.runtimeconfig.json");

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] WithBom(byte[] content) => [0xEF, 0xBB, 0xBF, .. content];
}
