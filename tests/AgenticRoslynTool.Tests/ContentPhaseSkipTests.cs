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

    [Fact]
    public void ContentPhase_NeverReportsARefusedPlanRowAsUnappliedWork()
    {
        // A row the plan phase refused carries the reason it was refused, and it stays in
        // the plan manifest because no content run writes that file. The report is a
        // different question: it lists work, so a refused row is never an unapplied split,
        // whether or not its path is supplied again.
        using var ws = new TempWorkspace();
        var splittable = ws.WriteFile("Pair.cs", "public class Pair { }\npublic class PairExtra { }\n");
        var solo = ws.WriteFile("Solo.cs", "public class Solo { }\n");
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var planOptions = new Options(ws.WriteInputList(splittable, solo), manifestPath, ws.Root, Phase.Plan, null);
        ManifestWriter.Write(new FileSplitter(planOptions).Run(), manifestPath, CollisionNaming.FileFirst);
        var plannedSolo = Assert.Single(ManifestWriter.Read(manifestPath), r => IsSolo(r.OriginalPath));
        Assert.Equal("skipped", plannedSolo.Status);

        // Route one: Solo.cs is not in this batch at all.
        var withoutSolo = Path.Combine(ws.Root, "batch1.txt");
        File.WriteAllLines(withoutSolo, new[] { splittable });
        var first = new FileSplitter(new Options(withoutSolo, manifestPath, ws.Root, Phase.Content, null)).Run();
        Assert.DoesNotContain(first, r => IsSolo(r.OriginalPath));

        // Route two: Solo.cs is supplied, but the plan never marked it split.
        var withSolo = Path.Combine(ws.Root, "batch2.txt");
        File.WriteAllLines(withSolo, new[] { solo });
        var second = new FileSplitter(new Options(withSolo, manifestPath, ws.Root, Phase.Content, null)).Run();
        var reported = Assert.Single(second, r => IsSolo(r.OriginalPath));
        Assert.Equal("skipped", reported.Status);
        Assert.Equal("not present as split in plan manifest", reported.Reason);
    }

    private static bool IsSolo(string path) =>
        string.Equals(Path.GetFileName(path), "Solo.cs", StringComparison.Ordinal);
}
