using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// When NextStep.For suggests a next phase, and what it carries. Decision 25.
public sealed class NextStepTests
{
    private static readonly FileResult SplitInPlace = FileResult.Split("/r/A.cs", "/r/A.cs", gitMove: false, note: null, [new NewFileResult("/r/B.cs", "B")]);

    private static readonly FileResult SplitWithRename = FileResult.Split("/r/Two.cs", "/r/A.cs", gitMove: true, note: null, [new NewFileResult("/r/B.cs", "B")]);

    private static readonly FileResult Skipped = FileResult.Skip("/r/C.cs", "/r/C.cs", "nothing to split");

    [Fact]
    public void Plan_WithARename_PointsAtRenames_AndCarriesEveryOption()
    {
        var options = new Options("/r/in put.txt", "/m/plan.csv", "/r", Phase.Plan, "// header", ["gen/", "obj/"], Json: true, CollisionNaming: CollisionNaming.TypeFirst);

        var args = NextStep.For(options, Phase.Plan, [SplitWithRename, Skipped]);

        Assert.Equal(
            ["split-types", "--phase", "renames", "--input", "/r/in put.txt", "--manifest", "/m/plan.csv", "--repo-root", "/r",
             "--require-header", "// header", "--exclude", "gen/", "--exclude", "obj/", "--json"],
            args);

        // The plan recorded the naming, so repeating it could only ever contradict the manifest.
        Assert.DoesNotContain("--collision-naming", args!);
    }

    [Fact]
    public void Plan_WithNoRename_SkipsStraightToContent()
    {
        var args = NextStep.For(Plain(), Phase.Plan, [SplitInPlace]);

        Assert.Equal(["split-types", "--phase", "content", "--input", "/r/list.txt", "--manifest", "/m/plan.csv", "--repo-root", "/r"], args);
    }

    [Fact]
    public void Renames_PointsAtContent_EvenWhenARowWasRenamed()
    {
        Assert.Equal("content", NextStep.For(Plain(), Phase.Renames, [SplitWithRename])![2]);
    }

    // Case names, not values: Phase and FileResult are internal, and a public test method
    // cannot take an internal type. AGENTS.md rule 7 forbids widening them.
    [Theory]
    [InlineData("content is the last phase")]
    [InlineData("a failed row needs a person")]
    [InlineData("nothing to split")]
    [InlineData("no rows")]
    public void ReturnsNull_WhenThereIsNoSafeNextPhase(string because)
    {
        var (phase, rows) = because switch
        {
            "content is the last phase" => (Phase.Content, new[] { SplitInPlace }),
            "a failed row needs a person" => (Phase.Plan, new[] { SplitInPlace, FileResult.Failed("/r/D.cs", "/r/D.cs", "boom") }),
            "nothing to split" => (Phase.Plan, new[] { Skipped }),
            _ => (Phase.Renames, Array.Empty<FileResult>()),
        };

        Assert.True(NextStep.For(Plain(), phase, rows) is null, because);
    }

    [Fact]
    public void ReturnsNull_ForStandardInput()
    {
        // A second process reads an empty stream, plans nothing, and exits 0.
        var options = Plain() with { InputPath = Options.StdinPath };

        Assert.Null(NextStep.For(options, Phase.Plan, [SplitInPlace]));
    }

    private static Options Plain() => new("/r/list.txt", "/m/plan.csv", "/r", Phase.Plan, null);
}
