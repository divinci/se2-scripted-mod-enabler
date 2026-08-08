using System;
using System.Collections.Generic;
using System.Text.Json;
using SE2ScriptedModEnabler.Setup;

namespace SE2ScriptedModEnabler.Cli;

/// <summary>
/// <code>
/// smesetup status    [options]   what is installed and whether the last run worked
/// smesetup install   [options]   copy the DLL and register it in DEV_PLUGINS
/// smesetup uninstall [options]   remove our DEV_PLUGINS entry and our files
/// smesetup repair    [options]   install again over the top; safe to re-run
///
///   --game-dir PATH      the Game2 folder, or the folder above it
///   --install-dir PATH   where the DLL lives; required off Windows
///   --plugin PATH        the built SE2ScriptedModEnabler.dll to copy in
///   --dry-run            say what would happen, write nothing
///   --json               machine-readable output
/// </code>
/// Exit codes: 0 success, 1 failure, 2 bad usage.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }

        var command = args[0];
        var json = false;
        var options = new InstallOptions();

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game-dir": options = options with { GameDir = Next(args, ref i) }; break;
                case "--install-dir": options = options with { InstallDir = Next(args, ref i) }; break;
                case "--plugin": options = options with { PluginSource = Next(args, ref i) }; break;
                case "--dry-run": options = options with { DryRun = true }; break;
                case "--json": json = true; break;
                default:
                    Console.Error.WriteLine($"unknown option: {args[i]}");
                    Usage();
                    return 2;
            }
        }

        try
        {
            return command switch
            {
                "status" => Status(options, json),
                "install" or "repair" => Act(Installer.Install(options), options, json),
                "uninstall" => Act(Installer.Uninstall(options), options, json),
                _ => Unknown(command),
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Status(InstallOptions options, bool json)
    {
        var report = Installer.Inspect(options);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, Files.Json));
            return report.Status is InstallStatus.Working ? 0 : 1;
        }

        Console.WriteLine($"{report.Status}: {report.Headline}");
        Console.WriteLine();

        Line("game", report.Game?.GameDir);
        Line("steam buildid", report.Game?.BuildId);
        Line("install dir", report.InstallDir);
        Line("plugin", report.PluginPathWindows + (report.PluginPresent ? "" : "   (MISSING)"));
        Line("registered", report.Registered ? "yes" : "no");

        if (report.DevPlugins.Count > 0)
        {
            Console.WriteLine("  DEV_PLUGINS:");
            foreach (var segment in report.DevPlugins) Console.WriteLine($"    {segment}");
        }

        if (report.LastRun is { } run)
        {
            Console.WriteLine();
            Console.WriteLine($"  last run: {run.State} at {run.TimestampUtc}");
            Line("game build", run.GameBuild);
            Line("supported", run.SupportedBuilds);
            foreach (var note in run.Notes) Console.WriteLine($"    - {note}");
        }

        if (report.Notes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  notes:");
            foreach (var note in report.Notes) Console.WriteLine($"    - {note}");
        }

        return report.Status is InstallStatus.Working ? 0 : 1;
    }

    private static int Act(ActionResult result, InstallOptions options, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { result, status = Installer.Inspect(options) }, Files.Json));
            return result.Ok ? 0 : 1;
        }

        foreach (var step in result.Steps) Console.WriteLine($"  {step}");
        Console.WriteLine();
        Console.WriteLine(result.Summary);
        return result.Ok ? 0 : 1;
    }

    private static void Line(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Console.WriteLine($"  {label,-14} {value}");
    }

    private static string Next(IReadOnlyList<string> args, ref int i)
    {
        if (++i >= args.Count) throw new ArgumentException($"{args[i - 1]} needs a value");
        return args[i];
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        Usage();
        return 2;
    }

    private static void Usage() => Console.Error.WriteLine(
        """
        smesetup status|install|uninstall|repair [options]

          --game-dir PATH      the Game2 folder, or the folder above it
          --install-dir PATH   where the DLL lives; required off Windows
          --plugin PATH        the built SE2ScriptedModEnabler.dll to copy in
          --dry-run            say what would happen, write nothing
          --json               machine-readable output
        """);
}
