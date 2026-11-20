using Xunit;

namespace AgenticRoslynTool.Tests;

// The content phase writes its rows back over the manifest. Every row keeps the hash the
// plan recorded, so a later phase still has something to hold the file to.
public sealed class ContentHashCarryTests : ContentHashTestBase
{
    [Fact]
    public void ContentRowForAFileItNeverRead_KeepsThePlannedHash()
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var excluded = Workspace.WriteFile(Path.Combine("gen", "Alpha.cs"), "namespace N;\n\npublic class Alpha\n{\n}\n\npublic class Beta\n{\n}\n");
        var planned = Plan(source, excluded);
        var listPath = Workspace.WriteInputList(source, excluded);
        File.Delete(source);

        var rows = new FileSplitter(new Options(listPath, ManifestPath, Workspace.Root, Phase.Content, null, ["gen/"])).Run().ManifestRows;

        var missing = Assert.Single(rows, r => r.OriginalPath == source);
        Assert.Equal("input file does not exist", missing.Reason);
        Assert.Equal(planned.Single(r => r.OriginalPath == source).ContentHash, missing.ContentHash);
        var skipped = Assert.Single(rows, r => r.OriginalPath == excluded);
        Assert.StartsWith("excluded by pattern", skipped.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.Single(r => r.OriginalPath == excluded).ContentHash, skipped.ContentHash);
    }

    [Fact]
    public void ContentReportRow_ForARefusedFileSuppliedByItsKeptPath_RepeatsTheManifestHash()
    {
        // After a rename, a directory scan supplies the kept path. Once the row is no longer
        // a split, the report row still has to find it to repeat its hash.
        var stuff = Workspace.WriteFile("Stuff.cs", Mismatched);
        var planned = Assert.Single(Plan(stuff));
        var keptPath = Path.Combine(Workspace.Root, "Order2.cs");
        File.Move(stuff, keptPath);
        File.AppendAllText(keptPath, "// edited after the rename\n");
        var refused = Assert.Single(RunAndWriteManifest(Phase.Content, stuff).ManifestRows);
        Assert.Equal("skipped", refused.Status);

        var row = Assert.Single(Content(keptPath).ReportRows);

        Assert.Equal("not present as split in plan manifest", row.Reason);
        Assert.Equal(planned.ContentHash, row.ContentHash);
    }

    [Fact]
    public void ContentReportRows_RepeatTheManifestHash_ForInputsThatAreNotApplied()
    {
        var split = Workspace.WriteFile("Foo.cs", TwoTypes);
        var single = Workspace.WriteFile("Single.cs", "namespace N;\n\npublic class Single\n{\n}\n");
        var planned = Plan(split, single);

        // Only the single-type file is supplied. It is not planned as a split, and the
        // planned split is not supplied. Both report rows carry the recorded hash.
        var report = Content(single).ReportRows;

        var notPlanned = Assert.Single(report, r => r.OriginalPath == single);
        Assert.Equal("not present as split in plan manifest", notPlanned.Reason);
        Assert.Equal(planned.Single(r => r.OriginalPath == single).ContentHash, notPlanned.ContentHash);
        var notSupplied = Assert.Single(report, r => r.OriginalPath == split);
        Assert.Equal("planned as split but not supplied to the content phase input", notSupplied.Reason);
        Assert.Equal(planned.Single(r => r.OriginalPath == split).ContentHash, notSupplied.ContentHash);
    }

    [Fact]
    public void SecondContentRun_ReportsAnAlreadySplitFileAsStale()
    {
        // The content phase rewrote the kept file, so its bytes no longer match the plan. A
        // second run cannot tell that from an outside edit and refuses it the same way.
        // Recorded as a known limit: the reason is accurate about the bytes, not the cause.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        Plan(source);
        RunAndWriteManifest(Phase.Content, source);
        var split = File.ReadAllBytes(source);

        var row = Assert.Single(RunAndWriteManifest(Phase.Content, source).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
        Assert.Equal(split, File.ReadAllBytes(source));
    }

    [Fact]
    public void BatchedContentRuns_KeepTheHashOfRowsNotYetApplied()
    {
        // A content run rewrites the manifest and must carry every unapplied row through
        // with its hash, or the second batch would be refused for a plan nobody changed.
        var first = Workspace.WriteFile("Foo.cs", TwoTypes);
        var second = Workspace.WriteFile("Alpha.cs", "namespace N;\n\npublic class Alpha\n{\n}\n\npublic class Beta\n{\n}\n");
        Plan(first, second);

        var firstBatch = RunAndWriteManifest(Phase.Content, first);
        Assert.Equal("split", Assert.Single(firstBatch.ReportRows, r => r.OriginalPath == first).Status);

        var secondBatch = RunAndWriteManifest(Phase.Content, second);

        Assert.Equal("split", Assert.Single(secondBatch.ReportRows, r => r.OriginalPath == second).Status);
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "Beta.cs")));
    }

    [Fact]
    public void EditedManifestOverUnchangedSource_IsStillCaughtByTheStructuralCheck()
    {
        // The hash proves the source did not change. It says nothing about the manifest, so
        // the structural comparison stays as the guard on an edited plan.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Plan(source);
        ManifestWriter.Write(planned.Select(r => r with { KeptPath = Path.Combine(Workspace.Root, "Elsewhere.cs") }).ToArray(), ManifestPath, CollisionNaming.FileFirst);

        var row = Assert.Single(Content(source).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.Equal("content phase plan does not match manifest", row.Reason);
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
    }
}
