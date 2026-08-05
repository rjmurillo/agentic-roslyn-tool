using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class ContentPhaseSkipTests
{
    [Fact]
    public void ContentPhase_SkipsInputsThatPlanDidNotMarkSplit_AndReportsNoFailures()
    {
        // A single-type file's plan status is "skipped", not "split". The content
        // phase must skip that input rather than fail the whole run. Program.cs
        // exits 0 unless a result starts with "failed", so a pure skip run is a
        // clean exit; this test pins that both halves of the contract hold.
        using var ws = new TempWorkspace();
        var multi =
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n";
        var single =
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Solo { }\n";
        var fooPath = ws.WriteFile("Foo.cs", multi);
        var soloPath = ws.WriteFile("Solo.cs", single);
        var soloBytes = File.ReadAllBytes(soloPath);
        var listPath = ws.WriteInputList(fooPath, soloPath);

        var results = ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv"));

        var byPath = results.ToDictionary(r => r.OriginalPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("split", byPath[fooPath].Status);
        Assert.Equal("skipped", byPath[soloPath].Status);
        Assert.Contains("not present as split", byPath[soloPath].Reason ?? string.Empty);

        Assert.DoesNotContain(results, r => r.Status.StartsWith("failed", StringComparison.OrdinalIgnoreCase));

        // The skipped file's bytes are untouched.
        Assert.True(soloBytes.SequenceEqual(File.ReadAllBytes(soloPath)));
    }
}
