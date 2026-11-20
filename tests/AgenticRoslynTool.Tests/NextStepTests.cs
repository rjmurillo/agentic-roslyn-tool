using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// nextArgs is a contract: a caller may run it without reading it. Decision 25.
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

    [Fact]
    public void Describe_QuotesSpaces_AndSaysToCommitAfterRenames()
    {
        IReadOnlyList<string> args = ["split-types", "--phase", "content", "--input", "/r/in put.txt"];

        Assert.Equal("Next: run <tool command> split-types --phase content --input \"/r/in put.txt\"", NextStep.Describe(args, Phase.Plan));
        Assert.StartsWith("Next: commit the renames, then run ", NextStep.Describe(args, Phase.Renames), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("// a \"quoted\" header")]
    [InlineData("// line one\n// line two")]
    public void Describe_FallsBackToASentence_WhenNoQuotingIsSafe(string header)
    {
        IReadOnlyList<string> args = ["split-types", "--phase", "content", "--require-header", header];

        var line = NextStep.Describe(args, Phase.Plan);

        Assert.Equal("Next: run --phase content with the same options. --json prints the exact arguments as nextArgs.", line);
        Assert.DoesNotContain('\n', line);
    }

    // The proof that matters: run nextArgs exactly as printed, twice, and the split is done.
    [Fact]
    public void FollowingNextArgs_RunsTheWholeWorkflow()
    {
        using var ws = new TempWorkspace();
        var source = ws.WriteFile(Path.Combine("my src", "Two.cs"), "namespace N;\n\npublic class A { }\n\npublic class B { }\n");
        var generated = ws.WriteFile(Path.Combine("my src", "gen", "Gen.cs"), "namespace N;\n\npublic class Gen { }\n\npublic class Other { }\n");
        var generatedBefore = File.ReadAllBytes(generated);
        GitFixture.InitAndTrack(ws.Root, source);
        var manifest = Path.Combine(ws.Root, "plan dir", "plan.csv");

        var plan = Run(ws, ["split-types", "--input", Path.Combine(ws.Root, "my src"), "--manifest", manifest, "--repo-root", ws.Root, "--exclude", "gen/", "--json"]);
        var renamesArgs = NextArgs(plan);
        Assert.Equal("renames", renamesArgs[2]);
        Assert.Contains("Next: run <tool command> split-types --phase renames", plan.StandardError, StringComparison.Ordinal);

        var renames = Run(ws, renamesArgs);
        var contentArgs = NextArgs(renames);
        Assert.Equal("content", contentArgs[2]);
        Assert.Contains("Next: commit the renames, then run", renames.StandardError, StringComparison.Ordinal);

        var content = Run(ws, contentArgs);
        using var report = JsonDocument.Parse(content.StandardOutput);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("nextArgs").ValueKind);
        Assert.Equal(1, report.RootElement.GetProperty("summary").GetProperty("split").GetInt32());
        Assert.DoesNotContain("Next:", content.StandardError, StringComparison.Ordinal);

        var dir = Path.Combine(ws.Root, "my src");
        Assert.Equal(["A.cs", "B.cs"], Directory.GetFiles(dir, "*.cs").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));
        Assert.Equal(generatedBefore, File.ReadAllBytes(generated));
    }

    private static Options Plain() => new("/r/list.txt", "/m/plan.csv", "/r", Phase.Plan, null);

    private static CliResult Run(TempWorkspace ws, IReadOnlyList<string> args)
    {
        var result = ToolProcess.RunIn(ws.Root, [.. args]);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result;
    }

    private static string[] NextArgs(CliResult result)
    {
        using var report = JsonDocument.Parse(result.StandardOutput);
        return report.RootElement.GetProperty("nextArgs").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }
}
