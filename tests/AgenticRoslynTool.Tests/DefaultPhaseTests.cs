using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A run with no --phase plans and changes no source file. Decision 23.
public sealed class DefaultPhaseTests
{
    [Fact]
    public void NoPhase_ParsesAsPlan()
    {
        Assert.Equal(Phase.Plan, Options.Parse(["--input", "list.txt"]).Phase);
        Assert.Equal(Phase.Plan, Options.Parse(["--input", "list.txt", "--dry-run"]).Phase);
        Assert.Equal(Phase.Content, Options.Parse(["--input", "list.txt", "--phase", "content"]).Phase);
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

            var result = ToolProcess.Run("split-types", "--input", directory, "--manifest", manifest, "--json");

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
}
