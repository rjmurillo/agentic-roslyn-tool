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
    public void ForgottenCollisionNamingFlag_IsFixedByRunningContentAgainWithIt()
    {
        // Two inputs both emit Shared.cs, planned type-first. Content without the flag skips
        // both rows. The plan keeps them as split, so the same command with the flag
        // applies them. No plan run in between.
        using var workspace = new TempWorkspace();
        var (_, _, listPath) = CollisionNamingFixture.WriteSharedPair(workspace, sharedTypes: "public class Shared { }\n");
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "plan.report.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath, "--collision-naming", "type-first").ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);

        var forgotten = Phase("content", workspace, listPath, manifestPath);

        Assert.Equal(0, forgotten.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.All(ManifestWriter.Read(reportPath), r =>
        {
            Assert.Equal("skipped", r.Status);
            Assert.Equal("content phase plan does not match manifest", r.Reason);
        });
        Assert.Equal(["A.cs", "B.cs"], CollisionNamingFixture.CsFiles(workspace));

        var retry = Phase("content", workspace, listPath, manifestPath, "--collision-naming", "type-first");

        Assert.Equal(0, retry.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.All(ManifestWriter.Read(reportPath), r => Assert.Equal("split", r.Status));
        Assert.Equal(["A.cs", "B.cs", "Shared.A.cs", "Shared.B.cs"], CollisionNamingFixture.CsFiles(workspace));
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
        Assert.Contains("is a symbolic link", content.StandardError, StringComparison.Ordinal);
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
        Assert.Contains("is the plan manifest under another name", content.StandardError, StringComparison.Ordinal);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(TwoTypes, File.ReadAllText(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportReachingThePlanThroughALinkWhoseTargetCrossesALinkedDirectory_IsRefused(bool swapRoles)
    {
        // real/plans holds the plan. "hop" links to real, and "via" links to hop/plans, so
        // the target of one link runs through another. Both spellings name one file.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var plans = Path.Combine(workspace.Root, "real", "plans");
        Directory.CreateDirectory(plans);
        var direct = Path.Combine(plans, "plan.csv");
        var hop = Path.Combine(workspace.Root, "hop");
        var via = Path.Combine(workspace.Root, "via");
        try
        {
            Directory.CreateSymbolicLink(hop, Path.Combine(workspace.Root, "real"));
            Directory.CreateSymbolicLink(via, Path.Combine(hop, "plans"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var aliased = Path.Combine(via, "plan.csv");
        var (manifestPath, reportPath) = swapRoles ? (aliased, direct) : (direct, aliased);
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(direct);
        Squat(workspace, "Beta.cs");

        var content = Phase("content", workspace, listPath, manifestPath, "--report", reportPath);

        Assert.Equal(3, content.ExitCode);
        Assert.Contains("is the plan manifest under another name", content.StandardError, StringComparison.Ordinal);
        Assert.Equal(planBytes, File.ReadAllBytes(direct));
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(direct)).Status);
    }

    [Fact]
    public void ReportReachingThePlanThroughALinkTargetThatClimbsOutOfALink_IsRefused()
    {
        // deep/inner holds nothing but is where "hop" points. "via" points at
        // hop/../plans, and the operating system resolves ".." from the real location of
        // hop's target, deep/inner, so via is deep/plans. Collapsing ".." as text gives
        // <root>/plans instead, which does not exist.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var plans = Path.Combine(workspace.Root, "deep", "plans");
        Directory.CreateDirectory(plans);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "deep", "inner"));
        var manifestPath = Path.Combine(plans, "plan.csv");
        var hop = Path.Combine(workspace.Root, "hop");
        var via = Path.Combine(workspace.Root, "via");
        try
        {
            Directory.CreateSymbolicLink(hop, Path.Combine(workspace.Root, "deep", "inner"));
            Directory.CreateSymbolicLink(via, Path.Combine("hop", "..", "plans"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);
        Assert.Equal(planBytes, File.ReadAllBytes(Path.Combine(via, "plan.csv")));
        Squat(workspace, "Beta.cs");

        var content = Phase("content", workspace, listPath, manifestPath, "--report", Path.Combine(via, "plan.csv"));

        Assert.Equal(3, content.ExitCode);
        Assert.Contains("is the plan manifest under another name", content.StandardError, StringComparison.Ordinal);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Empty(Directory.GetFiles(plans, "*.tmp"));
    }

    [Fact]
    public void ReportBesideThePlanInALinkedDirectory_IsAllowed()
    {
        // The same directory under another spelling is fine when the name differs. Only
        // the manifest's own entry is protected.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var plans = Path.Combine(workspace.Root, "plans");
        Directory.CreateDirectory(plans);
        var manifestPath = Path.Combine(plans, "plan.csv");
        var alias = Path.Combine(workspace.Root, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, plans);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", Path.Combine(alias, "run.csv"));

        Assert.Equal(0, content.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(Path.Combine(plans, "run.csv"))).Status);
        Assert.Equal(["plan.csv", "run.csv"], Directory.GetFiles(plans).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManifestThatIsASymbolicLink_IsRefusedAndThePlanIsKept(bool linkedParent)
    {
        // deep/plans holds the plan. The manifest path is a link to it whose target climbs
        // out of deep/inner with "..". With linkedParent the link is reached through a
        // linked directory, so resolving its target as text lands on a directory that does
        // not exist. Without it the target is absolute and runs through the same link.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var plans = Path.Combine(workspace.Root, "deep", "plans");
        var inner = Path.Combine(workspace.Root, "deep", "inner");
        Directory.CreateDirectory(plans);
        Directory.CreateDirectory(inner);
        var real = Path.Combine(plans, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, real).ExitCode);
        var planBytes = File.ReadAllBytes(real);
        var hop = Path.Combine(workspace.Root, "hop");
        string manifestPath;
        try
        {
            Directory.CreateSymbolicLink(hop, inner);
            if (linkedParent)
            {
                File.CreateSymbolicLink(Path.Combine(inner, "m.csv"), Path.Combine("..", "plans", "plan.csv"));
                manifestPath = Path.Combine(hop, "m.csv");
            }
            else
            {
                manifestPath = Path.Combine(workspace.Root, "m.csv");
                File.CreateSymbolicLink(manifestPath, Path.Combine(hop, "..", "plans", "plan.csv"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Squat(workspace, "Beta.cs");

        foreach (var phase in new[] { "content", "renames" })
        {
            var run = Phase(phase, workspace, listPath, manifestPath, "--report", real);

            Assert.Equal(3, run.ExitCode);
            Assert.Contains($"The manifest path {manifestPath} is a symbolic link", run.StandardError, StringComparison.Ordinal);
        }

        // The default report path is refused the same way. The rule is about the manifest.
        Assert.Equal(3, Phase("content", workspace, listPath, manifestPath).ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(real));
        Assert.Equal(TwoTypes, File.ReadAllText(input));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void WriteToAnAliasOfTheManifest_IsRefusedAndTheStagedFileIsRemoved()
    {
        // Prepare refuses this path before a run. Write must refuse it too, because the
        // alias can appear while the run is working.
        using var workspace = new TempWorkspace();
        var plans = Path.Combine(workspace.Root, "plans");
        Directory.CreateDirectory(plans);
        var manifestPath = Path.Combine(plans, "plan.csv");
        var planned = new[] { FileResult.Skip(manifestPath, manifestPath, "the plan") };
        ManifestWriter.Write(planned, manifestPath);
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

        var rows = new[] { FileResult.Skip(manifestPath, manifestPath, "the report") };
        foreach (var name in new[] { "plan.csv", "PLAN.CSV" })
        {
            var reason = ReportFile.Write(rows, Path.Combine(alias, name), manifestPath);

            Assert.Contains("is the plan manifest under another name", reason, StringComparison.Ordinal);
        }

        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(["plan.csv"], Directory.GetFiles(plans).Select(Path.GetFileName));
    }

    [Fact]
    public void ReportWithTheManifestsNameInAnotherDirectory_IsAllowed()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "other", "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var planBytes = File.ReadAllBytes(manifestPath);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", reportPath);

        Assert.Equal(0, content.ExitCode);
        Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(reportPath)).Status);
    }

    [Fact]
    public void ReportWithAVeryLongName_IsWritten()
    {
        // The staging file's name must not grow with the report's name.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, new string('r', 240) + ".csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", reportPath);

        Assert.Equal(0, content.ExitCode);
        Assert.Equal("split", Assert.Single(ManifestWriter.Read(reportPath)).Status);
    }

    [Fact]
    public void RelativeReportPath_ResolvesAgainstTheWorkingDirectory()
    {
        var options = Options.Parse(["--input", "list.txt", "--manifest", "plan.csv", "--report", Path.Combine("out", "run.csv")]);

        Assert.Equal(Path.GetFullPath(Path.Combine("out", "run.csv")), options.ReportPath);
    }

    [Fact]
    public void ManifestDirectoryTooLongToLookUp_FailsClosedAndThePlanIsKept()
    {
        // The plan sits in a directory whose path is so long that the plan's own name
        // fits under the path limit and the longer probe name does not. The report reaches
        // the same directory through a short link. Looking for the probe under the long
        // spelling fails, and a failed look must not read as "a different directory".
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var deep = workspace.Root;
        const int target = 4075;
        string manifestPath;
        var alias = Path.Combine(workspace.Root, "s");
        try
        {
            while (deep.Length < target)
            {
                deep = Path.Combine(deep, new string('d', Math.Min(200, target - deep.Length - 1)));
            }

            Directory.CreateDirectory(deep);
            manifestPath = Path.Combine(deep, "p.csv");
            ManifestWriter.Write([FileResult.Split(input, input, false, null, [new NewFileResult(Path.Combine(workspace.Root, "Beta.cs"), "Beta")])], manifestPath);
            Directory.CreateSymbolicLink(alias, deep);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A platform whose path limit is lower cannot build the layout at all.
            return;
        }

        Assert.Equal(target, deep.Length);
        var planBytes = File.ReadAllBytes(manifestPath);
        Squat(workspace, "Beta.cs");

        foreach (var phase in new[] { "content", "renames" })
        {
            var run = Phase(phase, workspace, listPath, manifestPath, "--report", Path.Combine(alias, "p.csv"));

            Assert.Equal(3, run.ExitCode);
            Assert.Contains("is the plan manifest under another name", run.StandardError, StringComparison.Ordinal);
            Assert.Equal(planBytes, File.ReadAllBytes(manifestPath));
        }

        Assert.Equal(["p.csv"], Directory.GetFiles(deep).Select(Path.GetFileName));
    }

    [Fact]
    public void ReportNameTheFileSystemRejects_StopsTheRunBeforeAnyWrite()
    {
        // The directory is fine and the name is too long, so the look at the report path
        // fails. That must stop the run before a source file changes.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", Path.Combine(workspace.Root, new string('r', 300) + ".csv"));

        Assert.Equal(3, content.ExitCode);
        Assert.Equal(string.Empty, content.StandardOutput.Trim());
        Assert.Equal(TwoTypes, File.ReadAllText(input));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Beta.cs")));
        Assert.Equal(["Alpha.cs", "inputs.txt", "plan.csv"], Directory.GetFiles(workspace.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PathThatCannotBeExamined_IsRefusedNotTreatedAsAbsent()
    {
        // A path the operating system will not look up is neither present nor absent.
        using var workspace = new TempWorkspace();
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var reportPath = Path.Combine(workspace.Root, "run.csv");
        var unreadable = Path.Combine(workspace.Root, "bad\0name.csv");
        var rows = new[] { FileResult.Skip(manifestPath, manifestPath, "any row") };

        var badManifest = Assert.Throws<InvalidOperationException>(() => ReportFile.Prepare(reportPath, unreadable));
        Assert.Contains("Cannot examine the manifest path", badManifest.Message, StringComparison.Ordinal);
        var badReport = Assert.Throws<InvalidOperationException>(() => ReportFile.Prepare(unreadable, manifestPath));
        Assert.Contains("Cannot examine the report path", badReport.Message, StringComparison.Ordinal);
        Assert.Contains("Cannot examine the report path", ReportFile.Write(rows, unreadable, manifestPath), StringComparison.Ordinal);
        Assert.Contains("Cannot examine the manifest path", ReportFile.Write(rows, reportPath, unreadable), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
    }

    [Fact]
    public void RenameOntoADirectory_IsAFailedRowAndTheFileStaysPut()
    {
        // git mv would move the file into the directory. The row must not claim the file
        // is at a path that holds a directory.
        using var workspace = new TempWorkspace();
        var misnamed = workspace.WriteFile("Zed.cs", OtherTwoTypes);
        var listPath = workspace.WriteInputList(misnamed);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var destination = Squat(workspace, "Gamma.cs");

        var renames = Phase("renames", workspace, listPath, manifestPath);

        Assert.Equal(1, renames.ExitCode);
        var row = Assert.Single(ManifestWriter.Read(Path.Combine(workspace.Root, "plan.report.csv")));
        Assert.Equal("failed", row.Status);
        Assert.Equal(misnamed, row.KeptPath);
        Assert.True(row.GitMove);
        Assert.Equal($"rename to {destination} failed: a directory is at that path", row.Reason);
        Assert.Equal(OtherTwoTypes, File.ReadAllText(misnamed));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Fact]
    public void ReportPathEndingInASeparator_StopsTheRunBeforeAnyWrite()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);
        var target = Path.Combine(workspace.Root, "newdir") + Path.DirectorySeparatorChar;

        var content = Phase("content", workspace, listPath, manifestPath, "--report", target);

        Assert.Equal(3, content.ExitCode);
        Assert.Contains("is a directory", content.StandardError, StringComparison.Ordinal);
        Assert.Equal(TwoTypes, File.ReadAllText(input));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void EmptyExistingFileAtTheReportPath_IsNotAReportAndIsKept()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", TwoTypes);
        var empty = workspace.WriteFile("Empty.cs", string.Empty);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        Assert.Equal(0, Phase("plan", workspace, listPath, manifestPath).ExitCode);

        var content = Phase("content", workspace, listPath, manifestPath, "--report", empty);

        Assert.Equal(3, content.ExitCode);
        Assert.Contains("Refusing to overwrite", content.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, new FileInfo(empty).Length);
        Assert.Equal(TwoTypes, File.ReadAllText(input));
    }

    [Fact]
    public void WriteThatCannotStage_ReturnsTheReasonAndCreatesNothing()
    {
        // The report directory cannot be created, because a file holds its name. This is
        // the arm a full disk or a changed permission reaches mid-run.
        using var workspace = new TempWorkspace();
        var blocker = workspace.WriteFile("blocker", "not a directory");
        var manifestPath = Path.Combine(workspace.Root, "plan.csv");
        var rows = new[] { FileResult.Skip(blocker, blocker, "any row") };

        var reason = ReportFile.Write(rows, Path.Combine(blocker, "run.csv"), manifestPath);

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Equal(["blocker"], Directory.GetFileSystemEntries(workspace.Root).Select(Path.GetFileName));
        Assert.Equal("not a directory", File.ReadAllText(blocker));
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
