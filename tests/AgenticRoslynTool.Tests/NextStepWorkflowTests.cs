using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// nextArgs is a contract: a caller may run it without reading it. Decision 26.
public sealed class NextStepWorkflowTests
{
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

    private static CliResult Run(TempWorkspace ws, IReadOnlyList<string> args)
    {
        var result = CliRunner.RunIn(ws.Root, [.. args]);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result;
    }

    private static string[] NextArgs(CliResult result)
    {
        using var report = JsonDocument.Parse(result.StandardOutput);
        return report.RootElement.GetProperty("nextArgs").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }
}
