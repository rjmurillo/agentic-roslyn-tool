using Xunit;

namespace AgenticRoslynTool.Tests;

// The renames phase against a real repository: git mv does not change bytes, so a renamed
// file still matches its plan, and an edited one is never renamed.
public sealed class ContentHashRenamesTests : ContentHashTestBase
{
    private readonly string _stuff;

    // Every test here renames Stuff.cs, so each starts from that file tracked in a repository.
    public ContentHashRenamesTests()
    {
        _stuff = Workspace.WriteFile("Stuff.cs", Mismatched);
        InitRepository("Stuff.cs");
    }

    [Fact]
    public void RealRenamesPhase_ThenContent_AppliesTheSplit()
    {
        var planned = Assert.Single(Plan(_stuff));

        var renames = RunAndWriteManifest(Phase.Renames, _stuff);

        var keptPath = Path.Combine(Workspace.Root, "Order2.cs");
        var renamed = Assert.Single(renames.ReportRows);
        Assert.Equal("split", renamed.Status);
        Assert.Equal(planned.ContentHash, renamed.ContentHash);
        Assert.True(File.Exists(keptPath));
        Assert.False(File.Exists(_stuff));

        var row = Assert.Single(Content(_stuff).ReportRows);

        Assert.Equal("split", row.Status);
        Assert.Equal(keptPath, row.KeptPath);
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "Line.cs")));
    }

    [Fact]
    public void EditBetweenPlanAndRenames_IsRefusedByRenames_NotLaunderedIntoANewPlan()
    {
        // The renames phase recomputes the plan and its rows replace the manifest. Without a
        // check there, the edited bytes would be hashed into the new manifest and the
        // content phase would apply an edit nobody reviewed.
        Plan(_stuff);

        File.WriteAllText(_stuff, Mismatched.Replace("class Line { }", "class Line { int x; }", StringComparison.Ordinal));
        var edited = File.ReadAllBytes(_stuff);

        var renamed = Assert.Single(RunAndWriteManifest(Phase.Renames, _stuff).ReportRows);

        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, renamed.Reason, StringComparison.Ordinal);
        Assert.Equal(_stuff, renamed.KeptPath);
        Assert.True(File.Exists(_stuff));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Order2.cs")));

        var row = Assert.Single(Content(_stuff).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.Equal(edited, File.ReadAllBytes(_stuff));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Line.cs")));
    }

    [Fact]
    public void ContentBeforeRenames_KeepsThePlannedHash_SoRenamesStillRefusesAnEdit()
    {
        // Content run out of order fails the row before it ever reads the file. That row
        // replaces the manifest row, so it has to keep the hash, or a later renames run
        // would have nothing to hold the file to.
        var planned = Assert.Single(Plan(_stuff));

        var early = Assert.Single(RunAndWriteManifest(Phase.Content, _stuff).ManifestRows);
        Assert.Equal("failed", early.Status);
        Assert.Contains("expected renamed file", early.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, early.ContentHash);

        File.AppendAllText(_stuff, "// edited\n");

        var renamed = Assert.Single(RunAndWriteManifest(Phase.Renames, _stuff).ManifestRows);
        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, renamed.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, renamed.ContentHash);
        Assert.True(File.Exists(_stuff));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Order2.cs")));
    }

    [Fact]
    public void RenamesHandedTheKeptPath_StillHoldsTheFileToItsPlan()
    {
        // After a real rename, a directory scan supplies Order2.cs, which the plan never
        // listed as an input. The row is found through its kept path.
        var planned = Assert.Single(Plan(_stuff));
        RunAndWriteManifest(Phase.Renames, _stuff);

        var keptPath = Path.Combine(Workspace.Root, "Order2.cs");
        File.AppendAllText(keptPath, "// edited after the rename\n");

        var row = Assert.Single(RunAndWriteManifest(Phase.Renames, keptPath).ManifestRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", row.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, row.ContentHash);
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Line.cs")));
    }

    [Fact]
    public void RenamesWithNoManifest_PlansAfresh()
    {
        // Nothing was planned, so there is nothing to be held to.

        var row = Assert.Single(RunAndWriteManifest(Phase.Renames, _stuff).ManifestRows);

        Assert.Equal("split", row.Status);
        Assert.True(ContentHash.IsWellFormed(row.ContentHash));
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "Order2.cs")));
    }
}
