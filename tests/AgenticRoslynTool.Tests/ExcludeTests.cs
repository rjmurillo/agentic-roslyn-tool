using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// --exclude replaced a hardcoded list of directory names inherited from the repository this
// tool was extracted from. These tests pin the replacement so the hardcoding cannot creep back.
public sealed class ExcludeTests
{
    [Fact]
    public void Parse_CollectsRepeatedExcludePatterns()
    {
        var options = Options.Parse(["--input", "list.txt", "--exclude", "obj/", "--exclude", "generated/"]);

        Assert.Equal(["obj/", "generated/"], options.Excludes);
    }

    [Fact]
    public void Parse_DefaultsToNoExcludes()
    {
        var options = Options.Parse(["--input", "list.txt"]);

        Assert.Empty(options.Excludes!);
    }

    [Fact]
    public void ExcludedFile_IsSkippedWithTheMatchingPatternAndIsLeftUntouched()
    {
        using var workspace = new TempWorkspace();

        var source = workspace.WriteFile(
            Path.Combine("generated", "Foo.cs"),
            "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n");
        var before = File.ReadAllBytes(source);

        var listPath = workspace.WriteInputList(source);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var options = new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null, ["generated/"]);

        var result = Assert.Single(new FileSplitter(options).Run().ReportRows);

        Assert.Equal("skipped", result.Status);
        Assert.Equal("excluded by pattern: generated/", result.Reason);
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public void ExcludePattern_MatchesRegardlessOfSeparatorStyleAndCase()
    {
        using var workspace = new TempWorkspace();

        var source = workspace.WriteFile(
            Path.Combine("Generated", "Foo.cs"),
            "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n");

        var listPath = workspace.WriteInputList(source);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var options = new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null, [@"generated\"]);

        var result = Assert.Single(new FileSplitter(options).Run().ReportRows);

        Assert.Equal("skipped", result.Status);
    }

    [Fact]
    public void PathsTheToolOnceHardcoded_AreEligibleWhenNoExcludeIsGiven()
    {
        using var workspace = new TempWorkspace();

        // These three shapes were hardcoded skips inherited from the repository this tool
        // was extracted from. Nothing may be excluded unless the caller asks for it.
        var paths = new[]
        {
            Path.Combine("docs", "samples", "Sample.cs"),
            Path.Combine("examples", "Ev2", "Thing.cs"),
            "ResourceSubjects.cs",
        };

        var sources = paths
            .Select(p => workspace.WriteFile(p, "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n"))
            .ToArray();

        var listPath = workspace.WriteInputList(sources);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var options = new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null);

        var results = new FileSplitter(options).Run().ReportRows;

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal("split", r.Status));
    }

    [Fact]
    public void NonMatchingExclude_LeavesTheFileEligibleForSplitting()
    {
        using var workspace = new TempWorkspace();

        var source = workspace.WriteFile(
            "Foo.cs",
            "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n");

        var listPath = workspace.WriteInputList(source);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var options = new Options(listPath, manifestPath, workspace.Root, Phase.Plan, null, ["obj/"]);

        var result = Assert.Single(new FileSplitter(options).Run().ReportRows);

        Assert.Equal("split", result.Status);
    }
}
