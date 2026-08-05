using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The content phase substitutes output paths taken from the plan manifest in place of the
// paths it computes itself. That substitution is what lets a reviewed cross-file collision
// rename survive into the content phase. It also means a manifest whose path was edited
// after review points the write somewhere the computed-path existence check never looked.
public sealed class ManifestPathTamperingTests
{
    [Fact]
    public void ContentPhase_RefusesWhenManifestPathTargetsAnExistingFile()
    {
        using var workspace = new TempWorkspace();

        var source = workspace.WriteFile(
            "Foo.cs",
            "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n");

        var victim = workspace.WriteFile("Victim.cs", "namespace N;\n\npublic class Victim\n{\n}\n");
        var victimBefore = File.ReadAllBytes(victim);

        var listPath = workspace.WriteInputList(source);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var planOptions = new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null);
        var planResults = new FileSplitter(planOptions).Run();
        ManifestWriter.Write(planResults, manifestPath);

        // Redirect the planned output for Bar onto an existing, unrelated file.
        var tampered = planResults
            .Select(r => r.Status != "split"
                ? r
                : r with { NewFiles = r.NewFiles.Select(f => f with { Path = victim }).ToArray() })
            .ToArray();
        ManifestWriter.Write(tampered, manifestPath);

        var contentOptions = new Options(listPath, manifestPath, workspace.Root, Phase.Content, null);
        var results = new FileSplitter(contentOptions).Run();

        var result = Assert.Single(results);
        Assert.Equal("skipped", result.Status);
        Assert.Contains("target path already exists", result.Reason);
        Assert.Equal(victimBefore, File.ReadAllBytes(victim));
    }

    [Fact]
    public void ContentPhase_RefusesWhenTwoManifestPathsCollide()
    {
        using var workspace = new TempWorkspace();

        var source = workspace.WriteFile(
            "Foo.cs",
            "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n\npublic class Baz\n{\n}\n");

        var listPath = workspace.WriteInputList(source);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var planOptions = new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null);
        var planResults = new FileSplitter(planOptions).Run();
        ManifestWriter.Write(planResults, manifestPath);

        // Point both new files at the same path. Left unchecked, the second write wins and
        // one type is lost from disk.
        var duplicate = Path.Combine(workspace.Root, "Merged.cs");
        var tampered = planResults
            .Select(r => r.Status != "split"
                ? r
                : r with { NewFiles = r.NewFiles.Select(f => f with { Path = duplicate }).ToArray() })
            .ToArray();
        ManifestWriter.Write(tampered, manifestPath);

        var contentOptions = new Options(listPath, manifestPath, workspace.Root, Phase.Content, null);

        var result = Assert.Single(new FileSplitter(contentOptions).Run());

        Assert.Equal("skipped", result.Status);
        Assert.Contains("target path collision within split", result.Reason);
        Assert.False(File.Exists(duplicate));
    }
}
