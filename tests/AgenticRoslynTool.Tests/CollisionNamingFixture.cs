using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgenticRoslynTool.Tests;

// Shared setup for the --collision-naming tests: the A.cs and B.cs pair that both emit
// Shared.cs, and one-line runners for a phase under a given naming.
internal static class CollisionNamingFixture
{
    public static (string APath, string BPath, string ListPath) WriteSharedPair(TempWorkspace ws, string sharedTypes)
    {
        var aPath = ws.WriteFile("A.cs", TestHeader.Copyright + "\nnamespace Sample.One;\n\npublic class A { }\n\n" + sharedTypes);
        var bPath = ws.WriteFile("B.cs", TestHeader.Copyright + "\nnamespace Sample.Two;\n\npublic class B { }\n\n" + sharedTypes);
        return (aPath, bPath, ws.WriteInputList(aPath, bPath));
    }

    // A null naming is an absent flag, which renames and content resolve from the manifest.
    public static IReadOnlyList<FileResult> RunPhase(TempWorkspace ws, string listPath, string manifestPath, Phase phase, CollisionNaming? naming)
    {
        var options = new Options(listPath, manifestPath, ws.Root, phase, null, CollisionNaming: naming);
        return new FileSplitter(options).Run().ReportRows;
    }

    // Plans and writes the manifest the way Program does, recording the naming the run used.
    public static IReadOnlyList<FileResult> PlanAndRecord(TempWorkspace ws, string listPath, string manifestPath, CollisionNaming? naming)
    {
        var outcome = new FileSplitter(new Options(listPath, manifestPath, ws.Root, Phase.Plan, null, CollisionNaming: naming)).Run();
        ManifestWriter.Write(outcome.ManifestRows, manifestPath, outcome.CollisionNaming);
        return outcome.ReportRows;
    }

    public static string[] CsFiles(TempWorkspace ws)
    {
        return Directory.GetFiles(ws.Root, "*.cs").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
    }

    public static FileResult Split(string original, string output)
    {
        return FileResult.Split(original, original, gitMove: false, note: null, new[] { new NewFileResult(output, "Shared") });
    }
}
