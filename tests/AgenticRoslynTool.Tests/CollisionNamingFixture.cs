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

    public static IReadOnlyList<FileResult> RunPhase(TempWorkspace ws, string listPath, string manifestPath, Phase phase, CollisionNaming naming)
    {
        var options = new Options(listPath, manifestPath, ws.Root, phase, null, CollisionNaming: naming);
        return new FileSplitter(options).Run().ReportRows;
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
