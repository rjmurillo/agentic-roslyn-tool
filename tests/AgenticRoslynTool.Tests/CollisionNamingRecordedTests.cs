using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The plan records --collision-naming in the manifest, and later phases reuse it. These run
// the real executable, because the value reaches the manifest and the report through
// Program.cs. Only the plan writes the manifest; renames and content write the report.
public sealed class CollisionNamingRecordedTests
{
    [Fact]
    public void PlanRecordsTypeFirst_AndRenamesAndContentReuseItWithoutTheFlag()
    {
        using var ws = new TempWorkspace();
        var (_, _, listPath) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var manifest = Path.Combine(ws.Root, "m.csv");
        var report = Options.DefaultReportPath(manifest);
        string[] common = ["split-types", "--input", listPath, "--manifest", manifest, "--repo-root", ws.Root, "--json"];

        var plan = CliRunner.RunIn(ws.Root, [.. common, "--collision-naming", "type-first"]);
        AssertRecorded(plan, manifest, "type-first");

        // Renames reruns the collision pass. Without the recorded value, a run that forgot
        // the flag would compute file-first names the reviewed plan never held.
        var renames = CliRunner.RunIn(ws.Root, [.. common, "--phase", "renames"]);
        AssertRecorded(renames, report, "type-first");
        Assert.Contains("Shared.A.cs", File.ReadAllText(report), StringComparison.Ordinal);
        Assert.DoesNotContain("A.Shared.cs", File.ReadAllText(report), StringComparison.Ordinal);

        var content = CliRunner.RunIn(ws.Root, [.. common, "--phase", "content"]);
        AssertRecorded(content, report, "type-first");
        Assert.Equal(["A.cs", "B.cs", "Shared.A.cs", "Shared.B.cs"], CollisionNamingFixture.CsFiles(ws));
    }

    [Theory]
    [InlineData("renames")]
    [InlineData("content")]
    public void AFlagThatContradictsThePlan_ExitsThreeAndTouchesNothing(string phase)
    {
        using var ws = new TempWorkspace();
        var (aPath, bPath, listPath) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var manifest = Path.Combine(ws.Root, "m.csv");
        string[] common = ["split-types", "--input", listPath, "--manifest", manifest, "--repo-root", ws.Root, "--json"];
        Assert.Equal(0, CliRunner.RunIn(ws.Root, [.. common, "--collision-naming", "type-first"]).ExitCode);
        var manifestBefore = File.ReadAllBytes(manifest);
        var aBefore = File.ReadAllBytes(aPath);
        var bBefore = File.ReadAllBytes(bPath);

        var result = CliRunner.RunIn(ws.Root, [.. common, "--phase", phase, "--collision-naming", "file-first"]);

        // Before the column, content skipped the two rows and exited 0.
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        var line = Assert.Single(result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.StartsWith("error: --collision-naming file-first contradicts the plan manifest, which was planned with type-first.", line, StringComparison.Ordinal);
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifest));
        Assert.False(File.Exists(Options.DefaultReportPath(manifest)), "a refused run writes no report");
        Assert.Equal(aBefore, File.ReadAllBytes(aPath));
        Assert.Equal(bBefore, File.ReadAllBytes(bPath));
        Assert.Equal(["A.cs", "B.cs"], CollisionNamingFixture.CsFiles(ws));
    }

    [Fact]
    public void AMatchingFlag_IsAccepted()
    {
        using var ws = new TempWorkspace();
        var (_, _, listPath) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var manifest = Path.Combine(ws.Root, "m.csv");
        string[] common = ["split-types", "--input", listPath, "--manifest", manifest, "--repo-root", ws.Root, "--json", "--collision-naming", "type-first"];
        Assert.Equal(0, CliRunner.RunIn(ws.Root, common).ExitCode);

        var content = CliRunner.RunIn(ws.Root, [.. common, "--phase", "content"]);

        AssertRecorded(content, Options.DefaultReportPath(manifest), "type-first");
        Assert.Equal(["A.cs", "B.cs", "Shared.A.cs", "Shared.B.cs"], CollisionNamingFixture.CsFiles(ws));
    }

    // The JSON field and the CSV column that run wrote must both carry the value: the plan
    // writes the manifest, and renames and content write the report.
    private static void AssertRecorded(CliResult result, string writtenFile, string expected)
    {
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(expected, report.RootElement.GetProperty("collisionNaming").GetString());
        Assert.Equal(expected, CollisionNamingFlag.Format(ManifestCollisionNaming.Read(writtenFile)!.Value));
        Assert.Equal(0, report.RootElement.GetProperty("summary").GetProperty("failed").GetInt32());
    }
}
