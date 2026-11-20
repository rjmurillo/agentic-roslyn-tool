using Xunit;

namespace AgenticRoslynTool.Tests;

// A refused row must report where the file is, never a rename target nothing applied. That
// is the original path, unless an earlier renames run moved the file.
public sealed class RefusedRenameLocationTests : IDisposable
{
    // Order2 does not own the name Stuff.cs, so the plan asks for a rename to Order2.cs.
    private const string Mismatched = "namespace N;\n\npublic class Order2 { }\n\npublic class Line { }\n";

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void PlanSkip_TargetPathAlreadyExists_ReportsTheOriginalPath()
    {
        var stuff = _workspace.WriteFile("Stuff.cs", Mismatched);
        _workspace.WriteFile("Line.cs", "namespace N;\n\npublic class Other { }\n");

        var row = Assert.Single(Plan(stuff, requiredHeader: null));

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("target path already exists", row.Reason, StringComparison.Ordinal);
        AssertReportsOriginal(stuff, row);
    }

    [Fact]
    public void PlanSkip_EmptyDirectiveShell_ReportsTheOriginalPath()
    {
        var stuff = _workspace.WriteFile("Stuff.cs", "namespace N;\n\npublic class Order2 { }\n\n#region R\npublic class Line { }\n#endregion\n");

        var row = Assert.Single(Plan(stuff, requiredHeader: null));

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("splitting would leave an empty directive shell", row.Reason, StringComparison.Ordinal);
        AssertReportsOriginal(stuff, row);
    }

    [Fact]
    public void PlanFailure_OutputVerificationThrows_ReportsTheOriginalPath()
    {
        // A header that is not a comment makes every output fail to parse.
        var stuff = _workspace.WriteFile("Stuff.cs", Mismatched);

        var row = Assert.Single(Plan(stuff, requiredHeader: "this is not a comment"));

        Assert.Equal("failed", row.Status);
        Assert.Contains("does not parse", row.Reason, StringComparison.Ordinal);
        AssertReportsOriginal(stuff, row);
    }

    [Fact]
    public void CollisionSkip_QualifiedCollision_ReportsTheOriginalPath()
    {
        var root = _workspace.Root;
        var target = Path.Combine(root, "Shared.cs");
        var results = new[]
        {
            Renamed(Path.Combine(root, "one", "A.cs"), Path.Combine(root, "one", "Order2.cs"), target),
            Renamed(Path.Combine(root, "two", "A.cs"), Path.Combine(root, "two", "Order2.cs"), target),
        };

        var resolved = SplitPlanner.ResolvePlannedOutputCollisions(results, CollisionNaming.TypeFirst);

        Assert.All(resolved, r => Assert.Equal("qualified output path collision requires manual handling", r.Reason));
        Assert.All(resolved, r => Assert.Equal(r.OriginalPath, r.KeptPath));
        Assert.All(resolved, r => Assert.False(r.GitMove));
    }

    [Fact]
    public void CollisionSkip_RefusedGeneratedName_ReportsTheOriginalPath()
    {
        var root = _workspace.Root;
        var target = Path.Combine(root, "Shared.cs");
        var results = new[]
        {
            Renamed(Path.Combine(root, "G.cs"), Path.Combine(root, "Order2.cs"), target),
            CollisionNamingFixture.Split(Path.Combine(root, "B.cs"), target),
        };

        var refused = SplitPlanner.ResolvePlannedOutputCollisions(results, CollisionNaming.TypeFirst)[0];

        Assert.Equal(CollisionNames.GeneratedNameReason, refused.Reason);
        Assert.Equal(results[0].OriginalPath, refused.KeptPath);
        Assert.False(refused.GitMove);
    }

    [Fact]
    public void PlanSplit_StillReportsTheRenameTarget()
    {
        // The fix touches refusals only. A split row still names where the plan will put the file.
        var stuff = _workspace.WriteFile("Stuff.cs", Mismatched);

        var row = Assert.Single(Plan(stuff, requiredHeader: null));

        Assert.Equal("split", row.Status);
        Assert.True(row.GitMove);
        Assert.Equal(Path.Combine(_workspace.Root, "Order2.cs"), row.KeptPath);
    }

    [Fact]
    public void ContentSkip_RunComputesAnUnplannedRename_ReportsTheOriginalPath()
    {
        // Planned under stylecop, Foo.cs keeps its name. Content under metadata computes a
        // rename to Foo`1.cs that never happened, and refuses the row.
        var source = _workspace.WriteFile("Foo.cs", FooWithBar);
        var listPath = _workspace.WriteInputList(source);

        var row = Assert.Single(FileNamingRuns.RunPlanThenContent(_workspace, listPath, ManifestPath, FileNamingConvention.StyleCop, FileNamingConvention.Metadata));

        Assert.Equal("skipped", row.Status);
        Assert.Equal(source, row.KeptPath);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void ContentSkip_AfterALandedRename_ReportsTheRenamedPath()
    {
        // Plan and renames under metadata move Foo.cs to Foo`1.cs. Content under stylecop
        // would keep Foo.cs, which no longer holds the file.
        var source = _workspace.WriteFile("Foo.cs", FooWithBar);
        var listPath = _workspace.WriteInputList(source);
        var renamed = Path.Combine(_workspace.Root, "Foo`1.cs");
        GitFixture.InitAndTrack(_workspace.Root, "Foo.cs");
        FileNamingRuns.RunPhase(_workspace, listPath, ManifestPath, Phase.Plan, FileNamingConvention.Metadata);
        FileNamingRuns.RunPhase(_workspace, listPath, ManifestPath, Phase.Renames, FileNamingConvention.Metadata);

        var row = Assert.Single(FileNamingRuns.RunPhase(_workspace, listPath, ManifestPath, Phase.Content, FileNamingConvention.StyleCop));

        Assert.Equal("skipped", row.Status);
        Assert.Equal(renamed, row.KeptPath);
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(source));
    }

    private const string FooWithBar = "namespace N;\n\npublic class Foo<T>\n{\n}\n\npublic class Bar\n{\n}\n";

    private string ManifestPath => Path.Combine(_workspace.Root, "manifest.csv");

    private IReadOnlyList<FileResult> Plan(string input, string? requiredHeader)
    {
        return new FileSplitter(new Options(_workspace.WriteInputList(input), ManifestPath, _workspace.Root, Phase.Plan, requiredHeader)).Run();
    }

    private static void AssertReportsOriginal(string original, FileResult row)
    {
        Assert.False(row.GitMove);
        Assert.Equal(original, row.KeptPath);
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(original)!, "Order2.cs")));
    }

    private static FileResult Renamed(string original, string kept, string output) =>
        FileResult.Split(original, kept, gitMove: true, note: null, new[] { new NewFileResult(output, "Shared") });
}
