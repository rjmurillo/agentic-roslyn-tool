using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The content phase rewrites files as it goes and writes the manifest once, at the end. So
// anything that escapes the per-file boundary strands every file already rewritten with no
// record of them, and the manifest is the plan of record an agent applies in batches. These
// tests pin the two states that reach that boundary without being a read, decode, or write
// failure: an inconsistent rename state, and a manifest edited into a shape the plan phase
// never produces.
public sealed class ContentPhaseRunIntegrityTests
{
    [Fact]
    public void InconsistentRenameState_IsAFailedRowRatherThanAnAbortedRun()
    {
        using var workspace = new TempWorkspace();

        // Kept type matches the file name, so this one needs no rename and splits outright.
        var splittable = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\n");

        // Kept type does not match the file name, so the plan asks for a git mv to Gamma.cs.
        // The renames phase is deliberately never run, which is the ordinary operator mistake.
        var needsRename = workspace.WriteFile("Zed.cs", "public class Gamma { }\npublic class Delta { }\n");

        var listPath = workspace.WriteInputList(splittable, needsRename);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var results = workspace.RunPlanThenContent(listPath, manifestPath);

        Assert.Equal(2, results.Count);
        var failed = Assert.Single(results, r => r.OriginalPath == needsRename);
        Assert.Equal("failed", failed.Status);
        Assert.Contains("expected renamed file", failed.Reason!, System.StringComparison.Ordinal);

        // The first input was already rewritten before the second one failed. Removing the
        // guard makes Run() throw, so this test fails through that path rather than through
        // these assertions; they pin that the guarded run still did the work it could.
        Assert.Equal("split", Assert.Single(results, r => r.OriginalPath == splittable).Status);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Beta.cs")));
    }

    [Fact]
    public void ManifestWithDuplicateTypeRows_IsAFailedRowRatherThanAnAbortedRun()
    {
        using var workspace = new TempWorkspace();

        var untouched = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\n");
        var tamperedInput = workspace.WriteFile("Kappa.cs", "public class Kappa { }\npublic class Lambda { }\n");

        var listPath = workspace.WriteInputList(untouched, tamperedInput);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var planOptions = new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null);
        var planResults = new FileSplitter(planOptions).Run().ReportRows;

        // Duplicate the planned type row. The plan phase never emits this, but a hand-edited
        // or merged manifest can, and the content phase keys its substitution map by type.
        var tampered = planResults
            .Select(r => r.OriginalPath != tamperedInput
                ? r
                : r with { NewFiles = r.NewFiles.Concat(r.NewFiles).ToArray() })
            .ToArray();
        ManifestWriter.Write(tampered, manifestPath);

        var contentOptions = new Options(listPath, manifestPath, workspace.Root, Phase.Content, null);
        var results = new FileSplitter(contentOptions).Run().ReportRows;

        Assert.Equal(2, results.Count);
        Assert.Equal("failed", Assert.Single(results, r => r.OriginalPath == tamperedInput).Status);
        Assert.Equal("split", Assert.Single(results, r => r.OriginalPath == untouched).Status);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Beta.cs")));

        // The throw happens while building the plan, before any output is written. Pinning
        // that keeps a regression that moved the substitution after the write from passing
        // here while leaving the failing input half applied.
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Lambda.cs")));
    }
}
