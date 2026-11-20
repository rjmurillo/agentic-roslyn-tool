using Xunit;

namespace AgenticRoslynTool.Tests;

// The renames phase never records a hash it computed. These cover the rows with nothing
// recorded to compare: a file the plan never listed, never read, or recorded badly.
public sealed class ContentHashRenamesUnplannedTests : ContentHashTestBase
{
    [Fact]
    public void RenamesRowForAnUnplannedFileItWouldNotSplit_RecordsNoHash()
    {
        // Renames must not record a hash it computed, even on a row that does nothing.
        var planned = Workspace.WriteFile("Foo.cs", TwoTypes);
        var added = Workspace.WriteFile("Single.cs", "namespace N;\n\npublic class Single\n{\n}\n");
        Plan(planned);

        var rows = RunPhase(Phase.Renames, planned, added);

        var row = Assert.Single(rows, r => r.OriginalPath == added);
        Assert.StartsWith("nothing to split", row.Reason, StringComparison.Ordinal);
        Assert.Null(row.ContentHash);
    }

    [Fact]
    public void RenamesRowForAPlannedFileItCannotRead_KeepsItsReasonAndThePlannedHash()
    {
        // Renames never reads a missing or an excluded file, so it has no bytes to compare.
        // The row keeps the reason it computed and the hash the plan recorded.
        var missing = Workspace.WriteFile("Foo.cs", TwoTypes);
        var excluded = Workspace.WriteFile(Path.Combine("gen", "Alpha.cs"), "namespace N;\n\npublic class Alpha\n{\n}\n\npublic class Beta\n{\n}\n");
        var planned = Plan(missing, excluded);
        var listPath = Workspace.WriteInputList(missing, excluded);
        File.Delete(missing);

        var rows = new FileSplitter(new Options(listPath, ManifestPath, Workspace.Root, Phase.Renames, null, ["gen/"])).Run();

        var gone = Assert.Single(rows, r => r.OriginalPath == missing);
        Assert.Equal("input file does not exist", gone.Reason);
        Assert.Equal(planned.Single(r => r.OriginalPath == missing).ContentHash, gone.ContentHash);
        var skipped = Assert.Single(rows, r => r.OriginalPath == excluded);
        Assert.StartsWith("excluded by pattern", skipped.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.Single(r => r.OriginalPath == excluded).ContentHash, skipped.ContentHash);
    }

    [Fact]
    public void RenamesRunTwice_OverAnUnchangedUnplannedFile_DoesNotCallItStale()
    {
        // The first run writes a row with no hash for a file the plan never listed. The
        // second run finds that row. Nothing changed and nothing would be done, so the
        // file is not stale.
        var planned = Workspace.WriteFile("Foo.cs", TwoTypes);
        var added = Workspace.WriteFile("Single.cs", "namespace N;\n\npublic class Single\n{\n}\n");
        Plan(planned);
        RunPhase(Phase.Renames, planned, added);

        var rows = RunPhase(Phase.Renames, planned, added);

        var row = Assert.Single(rows, r => r.OriginalPath == added);
        Assert.StartsWith("nothing to split", row.Reason, StringComparison.Ordinal);
        Assert.Null(row.ContentHash);
        Assert.Equal("split", Assert.Single(rows, r => r.OriginalPath == planned).Status);
    }

    [Fact]
    public void UnplannedFileThatGainsATypeBetweenRenamesRuns_IsRefusedForHavingNoPlanRow()
    {
        // The first run reported the file and left the manifest alone, so the plan still
        // has no row for it. Once the file would be split, it is refused: nothing was ever
        // reviewed.
        var planned = Workspace.WriteFile("Foo.cs", TwoTypes);
        var added = Workspace.WriteFile("Alpha.cs", "namespace N;\n\npublic class Alpha\n{\n}\n");
        Plan(planned);
        RunPhase(Phase.Renames, planned, added);

        File.WriteAllText(added, "namespace N;\n\npublic class Alpha\n{\n}\n\npublic class Beta\n{\n}\n");

        var row = Assert.Single(RunPhase(Phase.Renames, planned, added), r => r.OriginalPath == added);

        Assert.Equal("skipped", row.Status);
        Assert.Equal("stale plan: the plan manifest has no row for this file; re-run --phase plan", row.Reason);
        Assert.Null(row.ContentHash);
    }

    [Fact]
    public void RenamesRefusesAMalformedRecordedHash_EvenOnAFileItWouldNotSplit()
    {
        // A recorded hash that cannot be compared proves nothing about the file, so the row
        // says so, whatever renames would have done with the file.
        var single = Workspace.WriteFile("Single.cs", "namespace N;\n\npublic class Single\n{\n}\n");
        var planned = Plan(single);
        ManifestWriter.Write(planned.Select(r => r with { ContentHash = "sha256:XYZ" }).ToArray(), ManifestPath);

        var row = Assert.Single(RunPhase(Phase.Renames, single));

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("stale plan: the manifest row carries a malformed contentHash", row.Reason, StringComparison.Ordinal);
        Assert.Equal("sha256:XYZ", row.ContentHash);
    }

    [Fact]
    public void RenamesRefusesAFileTheManifestHasNoRowFor()
    {
        var planned = Workspace.WriteFile("Foo.cs", TwoTypes);
        var added = Workspace.WriteFile("Stuff.cs", Mismatched);
        InitRepository("Stuff.cs");
        Plan(planned);

        var rows = RunPhase(Phase.Renames, planned, added);

        Assert.Equal("split", Assert.Single(rows, r => r.OriginalPath == planned).Status);
        var refused = Assert.Single(rows, r => r.OriginalPath == added);
        Assert.Equal("skipped", refused.Status);
        Assert.Equal("stale plan: the plan manifest has no row for this file; re-run --phase plan", refused.Reason);
        Assert.Null(refused.ContentHash);
        Assert.True(File.Exists(added));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Order2.cs")));
    }
}
