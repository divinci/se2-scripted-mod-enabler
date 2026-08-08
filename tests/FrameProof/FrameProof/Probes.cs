using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using FrameProofStub;

namespace FrameProof;

/// <summary>
/// T10. Everything in <c>SE2ScriptedModEnabler.Plugin</c> rests on one claim about the
/// runtime: <em>a try/catch cannot catch a resolution failure caused by its own body,
/// but a try/catch one frame up can</em>. That claim is load-bearing — it is the whole
/// reason a future Keen rename degrades to a logged line instead of a game that will not
/// start — and it is the kind of claim that is easy to believe and hard to check.
///
/// <para>So this class references <see cref="Ghost"/> from an assembly that is deleted
/// after being built against, and runs four probes that differ only in which frame the
/// reference sits in. If probe 1 does not catch, or probe 3 does, the design is wrong
/// and has to change before anything ships.</para>
///
/// <para>Deliberately free of game types, so <c>tools/frame-proof.sh</c> can answer the
/// question in WSL in a second. The in-game run adds only the other half of the claim —
/// that the game still reaches the menu — and that needs a real launch.</para>
/// </summary>
public static class Probes
{
    private static readonly List<string> Findings = [];

    public static IReadOnlyList<string> Run()
    {
        Findings.Clear();
        Sanity();
        Probe("1 callee NoInlining, try one frame up", CatchesForNoInliningCallee, expectCaught: true);
        Probe("2 callee AggressiveInlining, try one frame up", CatchesForInlinedCallee, expectCaught: null);
        Probe("3 reference in the try's own frame", CatchesForItself, expectCaught: false);
        Probe("4 reference reached only on a branch never taken", CatchesForDeadBranch, expectCaught: false);
        return Findings;
    }

    /// <summary>True when no probe contradicted the frame rule.</summary>
    public static bool Holds(IReadOnlyList<string> findings)
    {
        foreach (var finding in findings)
            if (finding.Contains("UNEXPECTED", StringComparison.Ordinal)
                || finding.Contains("VACUOUS", StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    /// Same lesson as T2: a probe that cannot fail proves nothing. If the stub is still
    /// on disk then every "caught" below is just a method returning normally.
    /// </summary>
    private static void Sanity()
    {
        var beside = Path.Combine(
            Path.GetDirectoryName(typeof(Probes).Assembly.Location) ?? ".",
            "FrameProofStub.dll");

        Findings.Add(File.Exists(beside)
            ? $"VACUOUS: the stub is still present at {beside} — nothing below is evidence"
            : "stub absent, as required");
    }

    /// <summary>
    /// Runs one probe from a frame of its own and records which frame caught. The result
    /// is recorded rather than asserted: probe 2 in particular is a question about JIT
    /// heuristics, and the honest answer might be "the inliner declined".
    /// </summary>
    private static void Probe(string name, Func<string?> probe, bool? expectCaught)
    {
        string outcome;
        bool caught;

        try
        {
            var inner = probe();
            caught = inner is not null;
            outcome = inner ?? "no exception at all — the reference was never resolved";
        }
        catch (Exception ex)
        {
            caught = false;
            outcome = $"escaped its own frame, caught here instead — {Describe(ex)}";
        }

        var verdict = expectCaught is null ? "informational"
            : caught == expectCaught ? "as designed"
            : "UNEXPECTED — the frame rule does not hold as written";

        Findings.Add($"probe {name}: {outcome} [{verdict}]");
    }

    // ---- probe 1: the shape Plugin.cs actually uses -------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? CatchesForNoInliningCallee()
    {
        try
        {
            TouchNotInlined();
            return null;
        }
        catch (Exception ex)
        {
            return $"caught by the try one frame up — {Describe(ex)}";
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TouchNotInlined() => Findings.Add(Ghost.Speak());

    // ---- probe 2: what NoInlining is buying ---------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? CatchesForInlinedCallee()
    {
        try
        {
            TouchInlined();
            return null;
        }
        catch (Exception ex)
        {
            return $"caught by the try one frame up — {Describe(ex)}";
        }
    }

    /// <summary>
    /// If the inliner folds this into its caller, the caller now names Ghost and fails to
    /// JIT before its own catch exists — the exact regression NoInlining prevents. If the
    /// inliner instead declines because it cannot resolve the body, probe 2 looks like
    /// probe 1 and the attribute was belt-and-braces. Either answer is worth knowing;
    /// neither is worth relying on, which is why the attribute stays.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TouchInlined() => Findings.Add(Ghost.Speak());

    // ---- probe 3: the mistake this whole design exists to avoid --------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? CatchesForItself()
    {
        try
        {
            Findings.Add(Ghost.Speak());
            return null;
        }
        catch (Exception ex)
        {
            // Expected never to run. If it does, the JIT resolved lazily and the frame
            // discipline in Plugin.cs is unnecessary — still worth knowing, but it would
            // mean the comments there are wrong.
            return $"caught in its own frame — {Describe(ex)}";
        }
    }

    // ---- probe 4: resolution is per-method, not per-statement ----------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? CatchesForDeadBranch()
    {
        try
        {
            // Never true. A method is compiled whole, so the reference below is resolved
            // on entry regardless — which is why "we only call it when the version
            // matches" is not by itself a defence, and why BuildGate has to name no game
            // type at all rather than merely avoid using them on the reject path.
            if (Environment.GetEnvironmentVariable("SE2SME_NEVER_SET") == "1")
                Findings.Add(Ghost.Speak());
            return null;
        }
        catch (Exception ex)
        {
            return $"caught in its own frame — {Describe(ex)}";
        }
    }

    /// <summary>
    /// One line. The assembly-load messages carry a trailing newline and sometimes an
    /// embedded one, which would break the findings up when they land in JSON or a log.
    /// </summary>
    private static string Describe(Exception ex) =>
        $"{ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" ").Trim();
}
