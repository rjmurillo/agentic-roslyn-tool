using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Runs the real executable into the states where nextArgs is null and checks what the skill's
// done rule says about each. Three review rounds found a null that an exclusion list read as
// done; these pin the allow list instead.
public sealed class SkillDoneRuleTests
{
    [Fact]
    public void ContentGivenPartOfThePlan_IsNotDone()
    {
        // The second planned file was left out of the content input. The run exits 0 with
        // nextArgs null, and the report still owes that split.
        using var ws = new TempWorkspace();
        var one = ws.WriteFile("One.cs", "namespace N;\n\npublic class One { }\n\npublic class OneExtra { }\n");
        var two = ws.WriteFile("Two.cs", "namespace N;\n\npublic class Two { }\n\npublic class TwoExtra { }\n");
        var manifest = Path.Combine(ws.Root, "m.csv");
        Assert.Equal(0, Run(ws, "--input", ws.WriteInputList(one, two), "--manifest", manifest).ExitCode);

        var content = Run(ws, "--input", ws.WriteInputList(one), "--manifest", manifest, "--phase", "content");

        using var report = JsonDocument.Parse(content.StandardOutput);
        Assert.Equal(0, content.ExitCode);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("nextArgs").ValueKind);
        Assert.Contains(report.RootElement.GetProperty("files").EnumerateArray(), row => row.GetProperty("reason").GetString() == "planned as split but not supplied to the content phase input");
        Assert.False(SkillDoneRule.Holds(report.RootElement));
    }

    [Fact]
    public void ContentAfterExcludingAFailedFile_IsDone()
    {
        // The skill sends an input-has-syntax-errors row back to steps 1 and 2 with --exclude.
        // The new plan skips the file, so content reports it as never planned, which the done
        // rule accepts. A file the plan split and content then excluded is not done, and the
        // done rule must keep saying so.
        using var ws = new TempWorkspace();
        var good = ws.WriteFile("src/Good.cs", "namespace N;\n\npublic class Good { }\n\npublic class GoodExtra { }\n");
        var template = ws.WriteFile("templates/Program.cs", "namespace N;\n\npublic class $Name$ { }\npublic class Other { }\n");
        var list = ws.WriteInputList(good, template);
        var manifest = Path.Combine(ws.Root, "m.csv");
        Assert.Equal(1, Run(ws, "--input", list, "--manifest", manifest).ExitCode);

        Assert.Equal(0, Run(ws, "--input", list, "--manifest", manifest, "--exclude", "templates/").ExitCode);
        var content = Run(ws, "--input", list, "--manifest", manifest, "--exclude", "templates/", "--phase", "content");

        using var report = JsonDocument.Parse(content.StandardOutput);
        Assert.Equal(0, content.ExitCode);
        Assert.Equal(0, report.RootElement.GetProperty("summary").GetProperty("failed").GetInt32());
        Assert.True(SkillDoneRule.Holds(report.RootElement));
    }

    [Fact]
    public void RenamesWithNoPlanOnDisk_SuggestsNothing_AndWritesNoManifest()
    {
        // Content refuses to run without a plan manifest, so suggesting it would exit 3.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Two.cs", "namespace N;\n\npublic class Two { }\n\npublic class Extra { }\n");
        var manifest = Path.Combine(ws.Root, "m.csv");

        var renames = Run(ws, "--input", ws.WriteInputList(source), "--manifest", manifest, "--phase", "renames");

        using var report = JsonDocument.Parse(renames.StandardOutput);
        Assert.Equal(0, renames.ExitCode);
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("nextArgs").ValueKind);
        Assert.False(File.Exists(manifest));
        Assert.False(SkillDoneRule.Holds(report.RootElement));
    }

    private static CliResult Run(TempWorkspace ws, params string[] args) =>
        CliRunner.RunIn(ws.Root, ["split-types", .. args, "--repo-root", ws.Root, "--json"]);
}
