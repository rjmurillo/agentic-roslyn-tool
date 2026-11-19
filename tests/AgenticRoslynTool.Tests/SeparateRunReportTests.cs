using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The plan manifest is the reviewed artifact, so only the plan phase may write it. The
// renames and content phases record what they did in a separate report file. These run the
// real executable, because which file a run writes is decided in Program.cs.
public sealed class SeparateRunReportTests
{
    private const string TwoTypes = "public class Alpha { }\npublic class Beta { }\n";

    private const string OtherTwoTypes = "public class Gamma { }\npublic class Delta { }\n";

    [Fact]
    public void ContentSuccess_LeavesThePlanByteIdenticalAndWritesTheReport()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");

        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        Assert.False(File.Exists(reportPath));
        var planBytes = File.ReadAllBytes(manifestPath);

        var content = Phase("content", workspace, listPath, manifestPath);

        Assert.Equal(0, content.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Contains($"Report: {reportPath}", content.StandardError, StringComparison.Ordinal);
        var row = Assert.Single(ManifestWriter.Read(reportPath));
        Assert.Equal("split", row.Status);
        Assert.Equal(Path.Combine(workspace.Root, "Beta.cs"), Assert.Single(row.NewFiles).Path);
    }

    [Fact]
    public void ContentFailure_LeavesThePlanByteIdenticalAndReportsTheFailure()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");

        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);
        var squatter = Squat(workspace, "Beta.cs");

        var content = Phase("content", workspace, listPath, manifestPath);

        Assert.Equal(1, content.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(manifestPath)).Status);
        var failed = Assert.Single(ManifestWriter.Read(reportPath));
        Assert.Equal("failed", failed.Status);
        Assert.Equal(input, failed.OriginalPath);
        Assert.Equal(input, failed.KeptPath);
        Assert.False(string.IsNullOrWhiteSpace(failed.Reason));
        Assert.Empty(failed.NewFiles);
        Assert.Equal(TwoTypes, File.ReadAllText(input));
        Assert.True(Directory.Exists(squatter));
    }

    [Fact]
    public void FailedRow_IsAppliedByARetryOfTheSamePlan()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");

        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);
        var squatter = Squat(workspace, "Beta.cs");
        Assert.Equal(1, Phase("content", workspace, listPath, manifestPath).ExitCode);

        // The operator clears the obstacle and runs the same command again. No plan run.
        Directory.Delete(squatter);
        var retry = Phase("content", workspace, listPath, manifestPath);

        Assert.Equal(0, retry.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(reportPath)).Status);
        Assert.Equal("public class Alpha { }\n", File.ReadAllText(input));
        Assert.Equal("public class Beta { }\n", File.ReadAllText(Path.Combine(workspace.Root, "Beta.cs")));
    }

    [Fact]
    public void MixedRetry_SkipsTheAppliedRowAndAppliesTheFailedOne()
    {
        using var workspace = new TempWorkspace();
        var applied = workspace.WriteFile("Alpha.cs", TwoTypes);
        var failing = workspace.WriteFile("Gamma.cs", OtherTwoTypes);
        var listPath = workspace.WriteInputList(applied, failing);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");
        var beta = Path.Combine(workspace.Root, "Beta.cs");

        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);
        var squatter = Squat(workspace, "Delta.cs");

        Assert.Equal(1, Phase("content", workspace, listPath, manifestPath).ExitCode);
        var firstReport = ManifestWriter.Read(reportPath);
        Assert.Equal("split", Assert.Single(firstReport, r => r.OriginalPath == applied).Status);
        Assert.Equal("failed", Assert.Single(firstReport, r => r.OriginalPath == failing).Status);
        var appliedBytes = File.ReadAllBytes(applied);
        var betaBytes = File.ReadAllBytes(beta);

        Directory.Delete(squatter);
        var retry = Phase("content", workspace, listPath, manifestPath, "--json");

        // The applied row is a skip with its own reason. It is not a failure that would hide
        // the real retry behind exit 1, and its outputs are not written a second time.
        Assert.Equal(0, retry.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(appliedBytes, File.ReadAllBytes(applied));
        Assert.Equal(betaBytes, File.ReadAllBytes(beta));

        var secondReport = ManifestWriter.Read(reportPath);
        var skipped = Assert.Single(secondReport, r => r.OriginalPath == applied);
        Assert.Equal("skipped", skipped.Status);
        Assert.StartsWith("already applied", skipped.Reason, StringComparison.Ordinal);
        Assert.Equal("split", Assert.Single(secondReport, r => r.OriginalPath == failing).Status);
        Assert.Equal("public class Delta { }\n", File.ReadAllText(Path.Combine(workspace.Root, "Delta.cs")));

        using var json = JsonDocument.Parse(retry.StandardOutput);
        var summary = json.RootElement.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("split").GetInt32());
        Assert.Equal(1, summary.GetProperty("skipped").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());

        // The report file holds the same rows the JSON document prints under "files".
        var jsonRows = json.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => (f.GetProperty("originalPath").GetString(), f.GetProperty("status").GetString(), f.GetProperty("reason").GetString()))
            .ToArray();
        Assert.Equal(secondReport.Select(r => ((string?)r.OriginalPath, (string?)r.Status, r.Reason)).ToArray(), jsonRows);
    }

    [Fact]
    public void Renames_WritesTheReportAndNeverTheManifest()
    {
        // File names match their primary types, so no git process starts.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");

        // With no plan on disk, the renames phase must not create one. A manifest nobody
        // reviewed would let the content phase run against it.
        Assert.Equal(0, Phase("renames", workspace, listPath, manifestPath).ExitCode);
        Assert.False(File.Exists(manifestPath));
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(reportPath)).Status);
        Assert.Equal(3, Phase("content", workspace, listPath, manifestPath).ExitCode);

        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);
        Assert.Equal(0, Phase("renames", workspace, listPath, manifestPath).ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void RenameFailure_IsAFailedRowInTheReport()
    {
        // The workspace is not a git repository, so the rename the plan asks for cannot
        // land. That must be a row and exit 1, not an abort that writes no report.
        using var workspace = new TempWorkspace();
        var misnamed = workspace.WriteFile("Zed.cs", OtherTwoTypes);
        var fine = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(misnamed, fine);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");

        var renames = Phase("renames", workspace, listPath, manifestPath, "--json");

        Assert.Equal(1, renames.ExitCode);
        var report = ManifestWriter.Read(reportPath);
        var failed = Assert.Single(report, r => r.OriginalPath == misnamed);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(misnamed, failed.KeptPath);
        Assert.True(failed.GitMove);
        Assert.StartsWith($"rename to {Path.Combine(workspace.Root, "Gamma.cs")} failed: ", failed.Reason, StringComparison.Ordinal);
        Assert.Equal("split", Assert.Single(report, r => r.OriginalPath == fine).Status);
        Assert.True(File.Exists(misnamed));

        using var json = JsonDocument.Parse(renames.StandardOutput);
        Assert.Equal(reportPath, json.RootElement.GetProperty("report").GetString());
    }

    [Fact]
    public void ReportFlag_ChoosesWhereTheReportGoes()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "out", "run.csv");

        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var content = Phase("content", workspace, listPath, manifestPath, "--report", reportPath, "--json");

        Assert.Equal(0, content.ExitCode);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "plan.report.csv")));
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(reportPath)).Status);

        using var json = JsonDocument.Parse(content.StandardOutput);
        Assert.Equal(manifestPath, json.RootElement.GetProperty("manifest").GetString());
        Assert.Equal(reportPath, json.RootElement.GetProperty("report").GetString());
    }

    [Fact]
    public void PlanJson_CarriesANullReport()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");

        var plan = Phase("plan", workspace, listPath, manifestPath, "--json");

        Assert.Equal(0, plan.ExitCode);
        using var json = JsonDocument.Parse(plan.StandardOutput);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("report").ValueKind);
        Assert.DoesNotContain("Report:", plan.StandardError, StringComparison.Ordinal);
        Assert.Equal(["inputs.txt", "plan.csv"], Directory.GetFiles(workspace.Root, "*.csv").Concat(Directory.GetFiles(workspace.Root, "*.txt")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReportAtTheManifestPath_IsARejectedCommandLine()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", manifestPath);

        Assert.Equal(2, content.ExitCode);
        Assert.Equal(string.Empty, content.StandardOutput.Trim());
        Assert.Contains("error: --report and --manifest cannot name the same file.", content.StandardError, StringComparison.Ordinal);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(TwoTypes, File.ReadAllText(input));
    }

    [Fact]
    public void ReportDifferingFromTheManifestOnlyByCase_IsARejectedCommandLine()
    {
        // On a case-insensitive volume these are one file, and the tool does not probe the
        // volume. Refusing costs one exit 2. Accepting costs the plan.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", Path.Combine(workspace.Root, "PLAN.CSV"));

        Assert.Equal(2, content.ExitCode);
        Assert.Equal(TwoTypes, File.ReadAllText(input));
    }

    [Fact]
    public void ReportReachingTheManifestThroughALink_StopsTheRunBeforeAnyWrite()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);
        var link = Path.Combine(workspace.Root, "link.csv");
        try
        {
            File.CreateSymbolicLink(link, manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows without the symlink privilege. There is no link to guard against.
            return;
        }

        var content = Phase("content", workspace, listPath, manifestPath, "--report", link);

        Assert.Equal(3, content.ExitCode);
        Assert.Contains("reaches the plan manifest", content.StandardError, StringComparison.Ordinal);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(TwoTypes, File.ReadAllText(input));
    }

    [Fact]
    public void ReportNamingAnExistingFileThatIsNotAReport_StopsTheRunBeforeAnyWrite()
    {
        // The report path is caller supplied, so it could name the input list or a source
        // file. Neither may be replaced, and the run must not start.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var other = workspace.WriteFile("Solo.cs", "public class Solo { }\n");
        var listPath = workspace.WriteInputList(input);
        var listText = File.ReadAllText(listPath);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);

        foreach (var target in new[] { listPath, other, input })
        {
            var content = Phase("content", workspace, listPath, manifestPath, "--report", target, "--json");

            Assert.Equal(3, content.ExitCode);
            Assert.Equal(string.Empty, content.StandardOutput.Trim());
            Assert.Contains("Refusing to overwrite", content.StandardError, StringComparison.Ordinal);
        }

        Assert.Equal(listText, File.ReadAllText(listPath));
        Assert.Equal("public class Solo { }\n", File.ReadAllText(other));
        Assert.Equal(TwoTypes, File.ReadAllText(input));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Beta.cs")));
    }

    [Fact]
    public void UnwritableReportPath_StopsTheRunBeforeAnySourceIsRewritten()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var directory = Path.Combine(workspace.Root, "taken");
        Directory.CreateDirectory(directory);

        // One target is a directory. The other sits under a file, so its folder cannot exist.
        foreach (var target in new[] { directory, Path.Combine(listPath, "run.csv") })
        {
            var content = Phase("content", workspace, listPath, manifestPath, "--report", target);

            Assert.Equal(3, content.ExitCode);
            Assert.StartsWith("error: ", content.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", content.StandardError, StringComparison.Ordinal);
        }

        Assert.Equal(TwoTypes, File.ReadAllText(input));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Beta.cs")));
    }

    [Fact]
    public void AbortedRun_ChangesNothingAtTheReportPath()
    {
        // An aborted run did nothing, so it has nothing to report and nothing to remove.
        // The earlier report stays, and the exit code says this run wrote none.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        Assert.Equal(0, Phase("content", workspace, listPath, manifestPath).ExitCode);
        var reportBytes = File.ReadAllBytes(reportPath);

        File.Move(manifestPath, manifestPath + ".away");
        var aborted = Phase("content", workspace, listPath, manifestPath);

        Assert.Equal(3, aborted.ExitCode);
        Assert.DoesNotContain("Report:", aborted.StandardError, StringComparison.Ordinal);
        Assert.Equal(reportBytes, File.ReadAllBytes(reportPath));
        Assert.Equal(["plan.csv.away", "plan.report.csv"], Directory.GetFiles(workspace.Root, "plan*").Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReportAtAPathTheRunWritesASourceFileTo_KeepsTheSourceAndPrintsTheRows()
    {
        // Beta.cs does not exist when the run starts, so the first check passes. The run
        // then creates it. The report must not replace it, and the record of the run must
        // not be lost with the report.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var beta = Path.Combine(workspace.Root, "Beta.cs");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", beta, "--json");

        Assert.Equal(3, content.ExitCode);
        Assert.Equal("public class Beta { }\n", File.ReadAllText(beta));
        Assert.Contains($"error: report not written to {beta}: Refusing to overwrite", content.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Report:", content.StandardError, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(content.StandardOutput);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("report").ValueKind);
        Assert.Equal("split", Assert.Single(json.RootElement.GetProperty("files").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(["Alpha.cs", "Beta.cs", "inputs.txt", "plan.csv"], Directory.GetFiles(workspace.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReportNotWritten_PrintsTheRowsAsCsvWithoutJson()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var beta = Path.Combine(workspace.Root, "Beta.cs");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", beta);

        Assert.Equal(3, content.ExitCode);
        var printed = Path.Combine(workspace.Root, "printed.csv");
        // Console.WriteLine ends the document with one more newline, as it does for the plan.
        File.WriteAllText(printed, content.StandardOutput.TrimEnd() + "\n");
        var row = Assert.Single(ManifestWriter.Read(printed));
        Assert.Equal("split", row.Status);
        Assert.Equal(beta, Assert.Single(row.NewFiles).Path);
    }

    [Fact]
    public void ReportThatIsAHardLinkToThePlan_LeavesThePlanIntact()
    {
        // No path comparison can see a hard link. The plan survives because the report is
        // moved into place and never written through the existing file.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);
        var link = Path.Combine(workspace.Root, "hard.csv");
        if (!TryCreateHardLink(link, manifestPath))
        {
            // No ln on this machine, which means Windows. The move the test pins is the
            // same call there.
            return;
        }

        Squat(workspace, "Beta.cs");
        var content = Phase("content", workspace, listPath, manifestPath, "--report", link);

        Assert.Equal(1, content.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(manifestPath)).Status);
        Assert.Equal("failed", Assert.Single(ManifestWriter.Read(link)).Status);
    }

    [Fact]
    public void ReportReachingThePlanThroughALinkedDirectory_StopsTheRunBeforeAnyWrite()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var plans = Path.Combine(workspace.Root, "plans");
        Directory.CreateDirectory(plans);
        var manifestPath = Path.Combine(plans, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);
        var alias = Path.Combine(workspace.Root, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, plans);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var content = Phase("content", workspace, listPath, manifestPath, "--report", Path.Combine(alias, "plan.csv"));

        Assert.Equal(3, content.ExitCode);
        Assert.Contains("reaches the plan manifest", content.StandardError, StringComparison.Ordinal);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(TwoTypes, File.ReadAllText(input));
    }

    [Fact]
    public void ReportThatIsALinkToAnEarlierReport_IsRefusedAndTheTargetIsKept()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        Assert.Equal(0, Phase("content", workspace, listPath, manifestPath).ExitCode);
        var reportBytes = File.ReadAllBytes(reportPath);
        var link = Path.Combine(workspace.Root, "latest.csv");
        try
        {
            File.CreateSymbolicLink(link, reportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var retry = Phase("content", workspace, listPath, manifestPath, "--report", link);

        Assert.Equal(3, retry.ExitCode);
        Assert.Contains("is a symbolic link", retry.StandardError, StringComparison.Ordinal);
        Assert.Equal(reportBytes, File.ReadAllBytes(reportPath));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void SecondRun_ReplacesTheEarlierReportAndLeavesNoStagingFile()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        Assert.Equal(0, Phase("content", workspace, listPath, manifestPath).ExitCode);
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(reportPath)).Status);

        Assert.Equal(0, Phase("content", workspace, listPath, manifestPath).ExitCode);

        Assert.Equal("skipped", Assert.Single(ManifestWriter.Read(reportPath)).Status);
        Assert.Equal(["Alpha.cs", "Beta.cs", "inputs.txt", "plan.csv", "plan.report.csv"], Directory.GetFiles(workspace.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("plan.csv", "plan.report.csv")]
    [InlineData("plan", "plan.report.csv")]
    [InlineData("x.report.csv", "x.report.report.csv")]
    [InlineData("sa1402-split-manifest.csv", "sa1402-split-manifest.report.csv")]
    public void DefaultReportPath_IsBesideTheManifestAndNeverTheManifest(string manifestName, string reportName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "art-default-report");
        var manifestPath = Path.Combine(directory, manifestName);

        var reportPath = Options.DefaultReportPath(manifestPath);

        Assert.Equal(Path.Combine(directory, reportName), reportPath);
        Assert.NotEqual(manifestPath, reportPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportWithThePlanPhase_IsARejectedCommandLine()
    {
        // The plan phase writes the manifest and no report. Accepting the flag there would
        // be a silent no-op for a caller that cannot see the screen.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "run.csv");

        foreach (var planFlags in new[] { new[] { "--phase", "plan" }, ["--dry-run"] })
        {
            string[] args = ["split-types", "--input", listPath, "--repo-root", workspace.Root, "--manifest", manifestPath, "--report", reportPath, .. planFlags];
            var result = CliRunner.Run(args);

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("error: --report applies to the renames and content phases.", result.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(manifestPath));
            Assert.False(File.Exists(reportPath));
        }
    }

    [Fact]
    public void HelpAfterReport_IsTreatedAsAValue()
    {
        // --report takes a value, so the token after it is a path and never a meta option.
        var result = CliRunner.Run("split-types", "--report", "--help");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("error: Missing --input.", result.StandardError, StringComparison.Ordinal);
    }

    private static CliResult Phase(string phase, TempWorkspace workspace, string listPath, string manifestPath, params string[] extra)
    {
        string[] args = ["split-types", "--phase", phase, "--input", listPath, "--repo-root", workspace.Root, "--manifest", manifestPath, .. extra];
        return CliRunner.Run(args);
    }

    private static bool TryCreateHardLink(string link, string target)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("ln") { RedirectStandardError = true };
            start.ArgumentList.Add(target);
            start.ArgumentList.Add(link);
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode == 0 && File.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // A directory squatting on a planned output makes the content write throw, and leaves
    // the source bytes exactly as the plan saw them.
    private static string Squat(TempWorkspace workspace, string name)
    {
        var path = Path.Combine(workspace.Root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
