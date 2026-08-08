using System;
using FrameProof;

// Exit 0 when the frame rule held, 1 when a probe contradicted it or the stub was still
// present. tools/frame-proof.sh reads that; a human reads the lines.
var findings = Probes.Run();

foreach (var finding in findings)
    Console.WriteLine(finding);

var holds = Probes.Holds(findings);

Console.WriteLine();
Console.WriteLine(holds
    ? "frame rule holds — a try/catch one frame up catches what its own frame cannot"
    : "frame rule DID NOT hold — see above; src/SE2ScriptedModEnabler/Plugin.cs needs rethinking");

return holds ? 0 : 1;
