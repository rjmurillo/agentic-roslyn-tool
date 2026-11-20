using Xunit;

namespace AgenticRoslynTool.Tests;

// The renames phase recomputes the plan and its rows replace the manifest. A file that
// changed must stay refused, whatever the recomputed row says, until the plan runs again.
public sealed class ContentHashRenamesStaleTests : ContentHashTestBase
{
    [Fact]
    public void StaleRefusal_StaysRefused_WhenRenamesRunsAfterIt()
    {
        // The content phase reports its refusal and leaves the manifest alone. If renames
        // were not held to the manifest, it would plan the edited file afresh, and the
        // refusal must hold on every later run for as long as the edit stays.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));

        File.WriteAllText(source, TwoTypes.Replace("=> 1;", "=> 2;", StringComparison.Ordinal));
        var edited = File.ReadAllBytes(source);

        var refused = Assert.Single(RunPhase(Phase.Content, source));
        Assert.Equal("skipped", refused.Status);
        Assert.Equal(planned.ContentHash, refused.ContentHash);

        // No rename is planned for Foo.cs, so this phase starts no git process.
        var renamed = Assert.Single(RunPhase(Phase.Renames, source));
        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", renamed.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, renamed.ContentHash);

        var after = RunPhase(Phase.Content, source);
        Assert.Equal("skipped", Assert.Single(after).Status);
        Assert.Equal(planned.ContentHash, Assert.Single(after).ContentHash);
        Assert.Equal(edited, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));

        // A new plan is one way out. Undoing the edit is the other.
        Plan(source);
        Assert.Equal("split", Assert.Single(Content(source)).Status);
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

        var renamed = Assert.Single(RunPhase(Phase.Renames, source));
        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", renamed.Reason, StringComparison.Ordinal);
        Assert.Equal(plannedHash, renamed.ContentHash);

        var row = Assert.Single(Content(source));
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

        var blocked = Assert.Single(RunPhase(Phase.Renames, source));
        Assert.Equal("skipped", blocked.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", blocked.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, blocked.ContentHash);

        File.Delete(blocker);

        var again = Assert.Single(RunPhase(Phase.Renames, source));
        Assert.Equal("skipped", again.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, again.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, again.ContentHash);

        Assert.Equal("skipped", Assert.Single(Content(source)).Status);
        Assert.False(File.Exists(blocker));
    }

    [Fact]
    public void EditedFileThatNoLongerSplits_IsReportedAsStale_NotAsNothingToSplit()
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));

        File.WriteAllText(source, "namespace N;\n\npublic class Foo\n{\n}\n");

        var row = Assert.Single(RunPhase(Phase.Renames, source));

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", row.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, row.ContentHash);
    }

    [Fact]
    public void UnchangedFileThatRenamesSkips_KeepsItsReasonAndHash_AndThePlanStillHolds()
    {
        // Same bytes, so nothing is stale. The row keeps the reason renames computed and the
        // planned hash. The skip goes to the report and the plan is not written, so once
        // the obstacle is gone the same plan applies.
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));
        var blocker = Workspace.WriteFile("Bar.cs", "namespace N;\n");

        var blocked = Assert.Single(RunPhase(Phase.Renames, source));
        Assert.Equal("skipped", blocked.Status);
        Assert.StartsWith("target path already exists", blocked.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, blocked.ContentHash);

        File.Delete(blocker);

        var again = Assert.Single(RunPhase(Phase.Renames, source));
        Assert.Equal("split", again.Status);
        Assert.Null(again.Reason);
        Assert.Equal(planned.ContentHash, again.ContentHash);
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(ManifestPath)).Status);
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

        var rows = RunPhase(Phase.Renames, a, b);

        Assert.StartsWith(ContentHash.StalePlanPrefix, Assert.Single(rows, r => r.OriginalPath == a).Reason, StringComparison.Ordinal);
        var kept = Assert.Single(rows, r => r.OriginalPath == b);
        Assert.Equal("split", kept.Status);
        Assert.Equal(Path.Combine(Workspace.Root, "B.Shared.cs"), Assert.Single(kept.NewFiles).Path);
    }
}
