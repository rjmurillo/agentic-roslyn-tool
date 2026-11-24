using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A run with no --phase plans and changes no source file. Decision 25.
public sealed class DefaultPhaseTests
{
    [Fact]
    public void NoPhase_ParsesAsPlan()
    {
        Assert.Equal(Phase.Plan, Options.Parse(["--input", "list.txt"]).Phase);
        Assert.Equal(Phase.Plan, Options.Parse(["--input", "list.txt", "--dry-run"]).Phase);
        Assert.Equal(Phase.Content, Options.Parse(["--input", "list.txt", "--phase", "content"]).Phase);
    }

    [Fact]
    public void ReportWithNoPhase_IsABadCommandLine()
    {
        // The plan writes no report, so --report is refused there. With plan as the default,
        // that now covers a run that names no phase at all.
        var error = Assert.Throws<ArgumentException>(() => Options.Parse(["--input", "list.txt", "--report", "run.csv"]));
        Assert.Contains("--report applies to the renames and content phases", error.Message, StringComparison.Ordinal);
    }

    // The first call an agent makes usually carries no --phase. It used to rewrite source.
    // It must now plan, leave every source byte alone, and still write the manifest.
    [Fact]
    public void NoPhase_PlansAndLeavesSourceUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Two.cs");
            const string Text = "class A { }\nclass B { }\n";
            File.WriteAllText(source, Text);
            var manifest = Path.Combine(directory, "manifest.csv");

            var result = CliRunner.Run("split-types", "--input", directory, "--repo-root", directory, "--manifest", manifest, "--json");

            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("plan", document.RootElement.GetProperty("phase").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("split").GetInt32());
            Assert.Equal(Text, File.ReadAllText(source));
            Assert.Equal(source, Assert.Single(Directory.GetFiles(directory, "*.cs")));
            Assert.True(File.Exists(manifest));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NoPhaseNoJson_PrintsTheManifestCsvAndANextLine()
    {
        // The bare call an agent makes first. Standard output is the manifest, and standard
        // error says what to run next.
        using var ws = new TempWorkspace();
        ws.WriteFile("Two.cs", "class Two { }\nclass B { }\n");
        var manifest = Path.Combine(ws.Root, "m.csv");

        var result = CliRunner.Run("split-types", "--input", ws.Root, "--repo-root", ws.Root, "--manifest", manifest);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash,collisionNaming", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Next: run <tool command> split-types --phase content", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_ShowsThePlanDefaultAndTheWorkflow()
    {
        var help = CliRunner.Run("--help").StandardOutput;

        Assert.Contains("Defaults to plan, which changes no source file.", help, StringComparison.Ordinal);
        Assert.Contains("Typical workflow.", help, StringComparison.Ordinal);
        Assert.Contains("nextArgs in the report holds the exact arguments for the next step.", help, StringComparison.Ordinal);
        Assert.Contains("Exit 0 can still mean nothing happened.", help, StringComparison.Ordinal);
    }
}
