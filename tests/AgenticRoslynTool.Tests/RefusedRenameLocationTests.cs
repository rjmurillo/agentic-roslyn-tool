using Xunit;

namespace AgenticRoslynTool.Tests;

// A plan that wants to rename a kept file and then refuses the split must report where the
// file is. The file never moved, so keptPath is the original path, not the rename target.
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

    private IReadOnlyList<FileResult> Plan(string input, string? requiredHeader)
    {
        var manifest = Path.Combine(_workspace.Root, "manifest.csv");
        return new FileSplitter(new Options(_workspace.WriteInputList(input), manifest, _workspace.Root, Phase.Plan, requiredHeader)).Run();
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
