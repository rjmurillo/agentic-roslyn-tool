using Xunit;

namespace AgenticRoslynTool.Tests;

// The content phase reports its rows and never writes the manifest. Every row keeps the hash the
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

        var rows = new FileSplitter(new Options(listPath, ManifestPath, Workspace.Root, Phase.Content, null, ["gen/"])).Run();

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
        // After a rename, a directory scan supplies the kept path. The first refusal went
        // to the report, so the plan row is still a split and the second run finds it by
        // the kept path, refuses the same edit, and repeats the planned hash.
        var stuff = Workspace.WriteFile("Stuff.cs", Mismatched);
        var planned = Assert.Single(Plan(stuff));
        var keptPath = Path.Combine(Workspace.Root, "Order2.cs");
        File.Move(stuff, keptPath);
        File.AppendAllText(keptPath, "// edited after the rename\n");
        var refused = Assert.Single(RunPhase(Phase.Content, stuff));
        Assert.Equal("skipped", refused.Status);

        var row = Assert.Single(Content(keptPath));

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
        Assert.Equal(stuff, row.OriginalPath);
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
        var report = Content(single);

        var notPlanned = Assert.Single(report, r => r.OriginalPath == single);
        Assert.Equal("not present as split in plan manifest", notPlanned.Reason);
        Assert.Equal(planned.Single(r => r.OriginalPath == single).ContentHash, notPlanned.ContentHash);
        var notSupplied = Assert.Single(report, r => r.OriginalPath == split);
        Assert.Equal("planned as split but not supplied to the content phase input", notSupplied.Reason);
        Assert.Equal(planned.Single(r => r.OriginalPath == split).ContentHash, notSupplied.ContentHash);
    }

    [Fact]
    public void SecondContentRun_ReportsAnAlreadySplitFileAsAlreadyApplied()
    {
        // The content phase rewrote the kept file, so its bytes no longer match the plan.
        // A second run tells that from an outside edit: every planned output exists and the
        // file no longer declares the planned types. The row still carries the planned hash.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        Plan(source);
        RunPhase(Phase.Content, source);
        var split = File.ReadAllBytes(source);

        var row = Assert.Single(RunPhase(Phase.Content, source));

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("already applied", row.Reason, StringComparison.Ordinal);
        Assert.Equal(Assert.Single(ManifestWriter.Read(ManifestPath)).ContentHash, row.ContentHash);
        Assert.Equal(split, File.ReadAllBytes(source));
    }

    [Fact]
    public void BatchedContentRuns_KeepTheHashOfRowsNotYetApplied()
    {
        // A content run leaves the manifest alone and must report every unapplied row
        // with its hash, or the second batch would be refused for a plan nobody changed.
        var first = Workspace.WriteFile("Foo.cs", TwoTypes);
        var second = Workspace.WriteFile("Alpha.cs", "namespace N;\n\npublic class Alpha\n{\n}\n\npublic class Beta\n{\n}\n");
        Plan(first, second);

        var firstBatch = RunPhase(Phase.Content, first);
        Assert.Equal("split", Assert.Single(firstBatch, r => r.OriginalPath == first).Status);

        var secondBatch = RunPhase(Phase.Content, second);

        Assert.Equal("split", Assert.Single(secondBatch, r => r.OriginalPath == second).Status);
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "Beta.cs")));
    }

    [Fact]
    public void EditedManifestOverUnchangedSource_IsStillCaughtByTheStructuralCheck()
    {
        // The hash proves the source did not change. It says nothing about the manifest, so
        // the structural comparison stays as the guard on an edited plan.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Plan(source);
        ManifestWriter.Write(planned.Select(r => r with { KeptPath = Path.Combine(Workspace.Root, "Elsewhere.cs") }).ToArray(), ManifestPath);

        var row = Assert.Single(Content(source));

        Assert.Equal("skipped", row.Status);
        Assert.Equal("content phase plan does not match manifest", row.Reason);
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
    }
}
