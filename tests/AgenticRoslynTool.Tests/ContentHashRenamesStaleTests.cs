using Xunit;

namespace AgenticRoslynTool.Tests;

// The renames phase recomputes the plan and its rows replace the manifest. A file that
// changed must stay refused, whatever the recomputed row says, until the plan runs again.
public sealed class ContentHashRenamesStaleTests : ContentHashTestBase
{
    [Fact]
    public void StaleRefusal_StaysRefused_WhenRenamesRunsAfterIt()
    {
        // The content phase writes its refusal back to the manifest as a skipped row. If that
        // row stored the hash it found, or if renames only looked at split rows, a renames
        // run would plan the edited file afresh and the next content run would apply it.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));

        File.WriteAllText(source, TwoTypes.Replace("=> 1;", "=> 2;", StringComparison.Ordinal));
        var edited = File.ReadAllBytes(source);

        var refused = Assert.Single(RunAndWriteManifest(Phase.Content, source).ReportRows);
        Assert.Equal("skipped", refused.Status);
        Assert.Equal(planned.ContentHash, refused.ContentHash);

        // No rename is planned for Foo.cs, so this phase starts no git process.
        var renamed = Assert.Single(RunAndWriteManifest(Phase.Renames, source).ReportRows);
        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", renamed.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, renamed.ContentHash);

        var after = RunAndWriteManifest(Phase.Content, source);
        Assert.Equal("skipped", Assert.Single(after.ReportRows).Status);
        Assert.Equal(planned.ContentHash, Assert.Single(after.ManifestRows).ContentHash);
        Assert.Equal(edited, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));

        // The plan phase is the one way out.
        Plan(source);
        Assert.Equal("split", Assert.Single(Content(source).ReportRows).Status);
    }

    [Fact]
    public void FileThePlanSkipped_ThenGainsAType_IsRefusedByRenames()
    {
        // The plan recorded this file as nothing to split. An edit that adds a type would
        // make renames plan a split nobody reviewed, so the skipped row's hash holds it too.
        var source = Workspace.WriteFile("Foo.cs", "namespace N;\n\npublic class Foo\n{\n}\n");
        Assert.Equal("skipped", Assert.Single(Plan(source)).Status);

        var plannedHash = ManifestWriter.Read(ManifestPath)[0].ContentHash;
        File.WriteAllText(source, TwoTypes);

        var renamed = Assert.Single(RunAndWriteManifest(Phase.Renames, source).ReportRows);
        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", renamed.Reason, StringComparison.Ordinal);
        Assert.Equal(plannedHash, renamed.ContentHash);

        var row = Assert.Single(Content(source).ReportRows);
        Assert.Equal("skipped", row.Status);
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void EditedFileThatRenamesWouldSkipAnyway_IsReportedAsStale_AndStaysRefused()
    {
        // An edited file that renames would skip for an unrelated reason must not have its
        // new hash recorded, and must be reported as changed. Otherwise the unrelated
        // reason clears, the next renames run finds a match, and content applies the edit.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));

        File.WriteAllText(source, TwoTypes.Replace("=> 1;", "=> 2;", StringComparison.Ordinal));
        var blocker = Workspace.WriteFile("Bar.cs", "namespace N;\n");

        var blocked = Assert.Single(RunAndWriteManifest(Phase.Renames, source).ManifestRows);
        Assert.Equal("skipped", blocked.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", blocked.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, blocked.ContentHash);

        File.Delete(blocker);

        var again = Assert.Single(RunAndWriteManifest(Phase.Renames, source).ManifestRows);
        Assert.Equal("skipped", again.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, again.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, again.ContentHash);

        Assert.Equal("skipped", Assert.Single(Content(source).ReportRows).Status);
        Assert.False(File.Exists(blocker));
    }

    [Fact]
    public void EditedFileThatNoLongerSplits_IsReportedAsStale_NotAsNothingToSplit()
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));

        File.WriteAllText(source, "namespace N;\n\npublic class Foo\n{\n}\n");

        var row = Assert.Single(RunAndWriteManifest(Phase.Renames, source).ManifestRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", row.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, row.ContentHash);
    }

    [Fact]
    public void UnchangedFileThatRenamesSkips_KeepsItsReasonAndHash_AndIsNotSplitLater()
    {
        // Same bytes, so nothing is stale. The row keeps the reason renames computed and the
        // planned hash. Once the manifest says skipped, only a new plan makes it a split.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));
        var blocker = Workspace.WriteFile("Bar.cs", "namespace N;\n");

        var blocked = Assert.Single(RunAndWriteManifest(Phase.Renames, source).ManifestRows);
        Assert.Equal("skipped", blocked.Status);
        Assert.StartsWith("target path already exists", blocked.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, blocked.ContentHash);

        File.Delete(blocker);

        var again = Assert.Single(RunAndWriteManifest(Phase.Renames, source).ManifestRows);
        Assert.Equal("skipped", again.Status);
        Assert.Equal("stale plan: the plan manifest does not mark this file as split; re-run --phase plan", again.Reason);
        Assert.Equal(planned.ContentHash, again.ContentHash);
    }

    [Fact]
    public void StaleFileInRenames_LeavesTheOtherInputOnItsReviewedPath()
    {
        // Both inputs plan Shared.cs, so the reviewed plan qualifies both. One going stale
        // must not hand the other the unqualified name the reviewer never saw.
        var a = Workspace.WriteFile("A.cs", "namespace One;\n\npublic class A\n{\n}\n\npublic class Shared\n{\n}\n");
        var b = Workspace.WriteFile("B.cs", "namespace Two;\n\npublic class B\n{\n}\n\npublic class Shared\n{\n}\n");
        Plan(a, b);

        File.AppendAllText(a, "// edited\n");

        var rows = RunAndWriteManifest(Phase.Renames, a, b).ManifestRows;

        Assert.StartsWith(ContentHash.StalePlanPrefix, Assert.Single(rows, r => r.OriginalPath == a).Reason, StringComparison.Ordinal);
        var kept = Assert.Single(rows, r => r.OriginalPath == b);
        Assert.Equal("split", kept.Status);
        Assert.Equal(Path.Combine(Workspace.Root, "B.Shared.cs"), Assert.Single(kept.NewFiles).Path);
    }
}
