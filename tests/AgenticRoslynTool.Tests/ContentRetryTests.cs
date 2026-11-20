using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A content retry reads the same plan again, so it meets rows an earlier run already
// applied. Those must be a skip with their own reason. The skip must never widen into an
// exemption from the existence checks, so the states that only look applied are pinned too.
public sealed class ContentRetryTests
{
    private const string AlreadyApplied = "already applied: every planned output exists and this file no longer declares the planned types";

    [Fact]
    public void SecondContentRun_SkipsAnAppliedRowWithoutWriting()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\n");
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var beta = Path.Combine(workspace.Root, "Beta.cs");

        Assert.Equal("split", Assert.Single(workspace.RunPlanThenContent(listPath, manifestPath)).Status);
        var inputBytes = File.ReadAllBytes(input);
        var betaBytes = File.ReadAllBytes(beta);

        var retry = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run());

        Assert.Equal("skipped", retry.Status);
        Assert.Equal(AlreadyApplied, retry.Reason);
        Assert.Empty(retry.NewFiles);
        Assert.Equal(inputBytes, File.ReadAllBytes(input));
        Assert.Equal(betaBytes, File.ReadAllBytes(beta));
    }

    [Fact]
    public void SecondContentRun_SkipsAnAppliedRowThatWasRenamed()
    {
        // Decision 19: a directory scan after the rename supplies the kept path. The retry
        // must find the same row that way and report where the file now lives.
        using var workspace = new TempWorkspace();
        var source = Path.Combine(workspace.Root, "src");
        var input = workspace.WriteFile(Path.Combine("src", "Zed.cs"), "public class Gamma { }\npublic class Delta { }\n");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var plan = new FileSplitter(new Options(source, manifestPath, workspace.Root, Phase.Plan, null)).Run();
        ManifestWriter.Write(plan, manifestPath);
        Assert.True(Assert.Single(plan).GitMove);

        // Stand in for the renames phase, which needs a real repository to run git mv.
        var keptPath = Path.Combine(source, "Gamma.cs");
        File.Move(input, keptPath);

        var first = Assert.Single(new FileSplitter(new Options(source, manifestPath, workspace.Root, Phase.Content, null)).Run());
        Assert.Equal("split", first.Status);

        var retry = new FileSplitter(new Options(source, manifestPath, workspace.Root, Phase.Content, null)).Run();

        var row = Assert.Single(retry, r => PathComparison.Comparer.Equals(r.OriginalPath, input));
        Assert.Equal("skipped", row.Status);
        Assert.Equal(AlreadyApplied, row.Reason);
        Assert.Equal(keptPath, row.KeptPath);
        Assert.True(row.GitMove);
        Assert.DoesNotContain(retry, r => r.Status == "failed");
    }

    [Fact]
    public void ExistingOutputBesideAnUnsplitSource_IsStillRefusedAndNeverOverwritten()
    {
        // Rule 10. The planned output exists, but the source still declares the type the plan
        // moves out. That is a collision, not applied work, so the existence check answers.
        using var workspace = new TempWorkspace();
        var source = "public class Alpha { }\npublic class Beta { }\n";
        var input = workspace.WriteFile("Alpha.cs", source);
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        ManifestWriter.Write(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null)).Run(), manifestPath);
        var squatter = workspace.WriteFile("Beta.cs", "// unrelated file\n");

        var row = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run());

        Assert.Equal("skipped", row.Status);
        Assert.Equal($"target path already exists: {squatter}", row.Reason);
        Assert.Equal("// unrelated file\n", File.ReadAllText(squatter));
        Assert.Equal(source, File.ReadAllText(input));
    }

    [Fact]
    public void SourceLeftWithNoTypes_IsNotCalledAlreadyApplied()
    {
        // The planned output exists and the source declares none of the planned types,
        // because it declares nothing at all. An applied split always leaves the kept type
        // behind, so this is not one.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\n");
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        ManifestWriter.Write(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null)).Run(), manifestPath);
        workspace.WriteFile("Beta.cs", "public class Beta { }\n");
        File.WriteAllText(input, "// emptied by hand\n");

        var row = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run());

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OneOfTwoOutputsMissing_IsNotCalledAlreadyApplied()
    {
        // Every planned output must exist, not just one of them.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\npublic class Gamma { }\n");
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        Assert.Equal("split", Assert.Single(workspace.RunPlanThenContent(listPath, manifestPath)).Status);
        File.Delete(Path.Combine(workspace.Root, "Gamma.cs"));

        var row = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run());

        Assert.NotEqual(AlreadyApplied, row.Reason);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AppliedFileThatNoLongerParses_IsStaleNotAlreadyApplied()
    {
        // Every output exists and the kept file declares none of the planned types, but it
        // no longer parses. A file that does not parse cannot be shown to be applied work.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\n");
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        Assert.Equal("split", Assert.Single(workspace.RunPlanThenContent(listPath, manifestPath)).Status);
        File.WriteAllText(input, "public class Alpha { \n");

        var row = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run());

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EditedSourceThatStillDeclaresAPlannedType_IsStaleNotAlreadyApplied()
    {
        // The planned output exists and the source changed, which is half of what an applied
        // split looks like. The source still declares the type the plan moves out, so it is
        // not applied work.
        using var workspace = new TempWorkspace();
        var edited = "public class Alpha { }\npublic class Beta { }\n// edited\n";
        var input = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\n");
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        ManifestWriter.Write(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null)).Run(), manifestPath);
        var output = workspace.WriteFile("Beta.cs", "// someone else's file\n");
        File.WriteAllText(input, edited);

        var row = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run());

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
        Assert.Equal(edited, File.ReadAllText(input));
        Assert.Equal("// someone else's file\n", File.ReadAllText(output));
    }

    [Fact]
    public void AppliedFileThatNoLongerDecodes_IsStaleNotAlreadyApplied()
    {
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\n");
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        Assert.Equal("split", Assert.Single(workspace.RunPlanThenContent(listPath, manifestPath)).Status);
        File.WriteAllBytes(input, [0x2F, 0x2F, 0x20, 0xC3, 0x28, 0x0A]);

        var row = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run());

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOutputAfterAnAppliedSplit_IsNotCalledAlreadyApplied()
    {
        // The claim in the reason is that every planned output exists. When one was deleted
        // after the split, the claim is false and the row must not make it.
        using var workspace = new TempWorkspace();
        var input = workspace.WriteFile("Alpha.cs", "public class Alpha { }\npublic class Beta { }\n");
        var listPath = workspace.WriteInputList(input);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var beta = Path.Combine(workspace.Root, "Beta.cs");

        Assert.Equal("split", Assert.Single(workspace.RunPlanThenContent(listPath, manifestPath)).Status);
        File.Delete(beta);

        var row = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run());

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(beta));
    }
}
