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
