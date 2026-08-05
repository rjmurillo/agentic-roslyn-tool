using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// An agent points the tool at a tree, at a list, or at a pipe. Before these tests a
// directory path reached File.ReadLines and surfaced "Access to the path is denied",
// which sent the caller chasing a permissions problem that did not exist.
public sealed class InputSourceTests
{
    private const string TwoTypes = "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n";

    [Fact]
    public void DirectoryInput_DiscoversSourceFilesRecursively()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteFile(Path.Combine("src", "Foo.cs"), TwoTypes);
        workspace.WriteFile(Path.Combine("src", "nested", "Baz.cs"), TwoTypes);

        var results = Plan(workspace, Path.Combine(workspace.Root, "src"));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("split", r.Status));
    }

    [Fact]
    public void DirectoryInput_SkipsBinAndObjBecauseTheBuildOwnsThem()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteFile(Path.Combine("src", "Foo.cs"), TwoTypes);
        workspace.WriteFile(Path.Combine("src", "obj", "Generated.cs"), TwoTypes);
        workspace.WriteFile(Path.Combine("src", "bin", "Debug", "Copied.cs"), TwoTypes);

        var results = Plan(workspace, Path.Combine(workspace.Root, "src"));

        var only = Assert.Single(results);
        Assert.Equal(Path.Combine(workspace.Root, "src", "Foo.cs"), only.OriginalPath);
    }

    [Fact]
    public void DirectoryInput_IgnoresNonSourceFiles()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteFile(Path.Combine("src", "Foo.cs"), TwoTypes);
        workspace.WriteFile(Path.Combine("src", "readme.md"), "not code");
        workspace.WriteFile(Path.Combine("src", "Foo.csproj"), "<Project />");

        Assert.Single(Plan(workspace, Path.Combine(workspace.Root, "src")));
    }

    [Fact]
    public void DirectoryInput_DefaultsTheManifestOutsideTheScannedTree()
    {
        using var workspace = new TempWorkspace();
        var scanned = Path.Combine(workspace.Root, "src");
        Directory.CreateDirectory(scanned);

        var options = Options.Parse(["--input", scanned, "--repo-root", workspace.Root]);

        Assert.Equal(Path.Combine(workspace.Root, "sa1402-split-manifest.csv"), options.ManifestPath);
    }

    [Fact]
    public void StdinInput_ReadsOnePathPerLine()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);

        var original = Console.In;
        try
        {
            Console.SetIn(new StringReader(source + Environment.NewLine));
            var only = Assert.Single(Plan(workspace, Options.StdinPath));
            Assert.Equal(source, only.OriginalPath);
        }
        finally
        {
            Console.SetIn(original);
        }
    }

    [Fact]
    public void StdinInput_IsNotResolvedAsAFilePath()
    {
        var options = Options.Parse(["--input", Options.StdinPath, "--repo-root", Path.GetTempPath()]);

        Assert.Equal(Options.StdinPath, options.InputPath);
        Assert.Equal(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "sa1402-split-manifest.csv"), options.ManifestPath);
    }

    [Fact]
    public void NonUtf8Input_IsSkippedRatherThanEndingTheRun()
    {
        using var workspace = new TempWorkspace();

        // Latin-1 byte 0xE9 with no byte order mark. The reader is strict UTF-8, so this
        // throws on decode. It must land as a skipped row, not a stack trace mid-run.
        var path = Path.Combine(workspace.Root, "Latin1.cs");
        File.WriteAllBytes(path, [.. "// caf"u8.ToArray(), 0xE9, .. "\npublic class A { }\npublic class B { }\n"u8.ToArray()]);

        var results = Plan(workspace, workspace.WriteInputList(path));

        var result = Assert.Single(results);
        Assert.Equal("skipped", result.Status);
        Assert.Contains("not valid UTF-8", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateInputPaths_ProduceOneRow()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.WriteFile("Dup.cs", "public class A { }\npublic class B { }\n");

        var results = Plan(workspace, workspace.WriteInputList(path, path));

        Assert.Single(results);
    }

    // The two strings differ but name one file. De-duplicating before resolving them let
    // the same file be split twice, and the second pass sees a file that is already split.
    [Fact]
    public void DuplicateInputPathsInDifferentTextualForms_ProduceOneRow()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.WriteFile("Dup.cs", "public class A { }\npublic class B { }\n");
        var directory = Path.GetDirectoryName(path)!;
        var roundabout = Path.Combine(directory, "sub", "..", Path.GetFileName(path));

        Assert.NotEqual(path, roundabout);

        var results = Plan(workspace, workspace.WriteInputList(path, roundabout));

        Assert.Single(results);
    }

    [Fact]
    public void SingleSourceFileInput_IsTheFileToSplitNotAListOfPaths()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Pair.cs", "public class Pair { }\npublic class Other { }\n");

        // Reading this as a line list would feed lines of C# in as paths and report a
        // screen of "does not exist" skips at exit code 0.
        var results = Plan(workspace, source);

        var result = Assert.Single(results);
        Assert.Equal("split", result.Status);
    }

    [Fact]
    public void ContentPhase_ReportsAPlannedFileTheInputNeverSupplied()
    {
        using var workspace = new TempWorkspace();
        var planned = workspace.WriteFile("Planned.cs", "public class Planned { }\npublic class Extra { }\n");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var planOptions = new Options(workspace.WriteInputList(planned), manifestPath, workspace.Root, Phase.Plan, null);
        ManifestWriter.Write(new FileSplitter(planOptions).Run().ReportRows, manifestPath);

        // Content phase runs with an empty input list. The plan row must not vanish.
        var emptyList = Path.Combine(workspace.Root, "empty.txt");
        File.WriteAllText(emptyList, string.Empty);
        var contentOptions = new Options(emptyList, manifestPath, workspace.Root, Phase.Content, null);
        var results = new FileSplitter(contentOptions).Run().ReportRows;

        var carried = Assert.Single(results);
        Assert.Equal("skipped", carried.Status);
        Assert.Contains("not supplied to the content phase input", carried.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentPhase_KeepsAnUnplannedInputOutOfTheRewrittenManifest()
    {
        using var workspace = new TempWorkspace();
        var planned = workspace.WriteFile("Planned.cs", "public class Planned { }\npublic class PlannedExtra { }\n");
        var unplanned = workspace.WriteFile("Unplanned.cs", "public class Unplanned { }\n");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var planOptions = new Options(workspace.WriteInputList(planned), manifestPath, workspace.Root, Phase.Plan, null);
        ManifestWriter.Write(new FileSplitter(planOptions).Run().ManifestRows, manifestPath);

        var contentOptions = new Options(workspace.WriteInputList(planned, unplanned), manifestPath, workspace.Root, Phase.Content, null);
        var outcome = new FileSplitter(contentOptions).Run();

        // The caller has to see the mismatch, but the manifest is the plan of record and must
        // not accumulate rows the plan phase never produced.
        Assert.Contains(outcome.ReportRows, r => string.Equals(Path.GetFileName(r.OriginalPath), "Unplanned.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.ManifestRows, r => string.Equals(Path.GetFileName(r.OriginalPath), "Unplanned.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void ContentPhase_KeepsAnUnappliedPlanRowUsableByALaterBatch()
    {
        using var workspace = new TempWorkspace();
        var first = workspace.WriteFile("First.cs", "public class First { }\npublic class FirstExtra { }\n");
        var second = workspace.WriteFile("Second.cs", "public class Second { }\npublic class SecondExtra { }\n");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var planOptions = new Options(workspace.WriteInputList(first, second), manifestPath, workspace.Root, Phase.Plan, null);
        ManifestWriter.Write(new FileSplitter(planOptions).Run().ManifestRows, manifestPath);

        // Batch one applies only First.cs and rewrites the manifest it read.
        var batchOne = Path.Combine(workspace.Root, "batch1.txt");
        File.WriteAllLines(batchOne, new[] { first });
        var firstOutcome = new FileSplitter(new Options(batchOne, manifestPath, workspace.Root, Phase.Content, null)).Run();
        ManifestWriter.Write(firstOutcome.ManifestRows, manifestPath);

        // Batch two reads the rewritten manifest. Second.cs must still be actionable, or
        // the reviewed plan is destroyed by the act of applying it in pieces.
        var batchTwo = Path.Combine(workspace.Root, "batch2.txt");
        File.WriteAllLines(batchTwo, new[] { second });
        var secondOutcome = new FileSplitter(new Options(batchTwo, manifestPath, workspace.Root, Phase.Content, null)).Run();

        var applied = Assert.Single(secondOutcome.ReportRows, r => string.Equals(Path.GetFileName(r.OriginalPath), "Second.cs", StringComparison.Ordinal));
        Assert.Equal("split", applied.Status);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "SecondExtra.cs")));
    }

    [Fact]
    public void DirectoryScan_IncludesASymlinkWhoseTargetIsOutsideTheScannedTree()
    {
        using var outside = new TempWorkspace();
        using var workspace = new TempWorkspace();
        var target = outside.WriteFile("Target.cs", "public class Target { }\npublic class Second { }\n");
        var link = Path.Combine(workspace.Root, "Linked.cs");

        if (!TryCreateSymbolicLink(link, target))
        {
            return;
        }

        var results = Plan(workspace, workspace.Root);

        Assert.Contains(results, r => string.Equals(Path.GetFileName(r.OriginalPath), "Linked.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectoryScan_KeepsTheRealPathWhenASymlinkAndItsTargetAreBothScanned()
    {
        using var workspace = new TempWorkspace();
        var target = workspace.WriteFile("real/Target.cs", "public class Target { }\npublic class Second { }\n");
        var link = Path.Combine(workspace.Root, "Linked.cs");

        if (!TryCreateSymbolicLink(link, target))
        {
            return;
        }

        // Two paths, one file. Splitting it twice would apply the plan through the link and
        // then report the real path as having nothing to do.
        var results = Plan(workspace, workspace.Root);

        var result = Assert.Single(results);
        Assert.Equal("Target.cs", Path.GetFileName(result.OriginalPath));
    }

    /// <summary>
    /// Windows needs developer mode or elevation to create a symlink. Returns false when the
    /// platform refuses, so the calling test opts out; the Linux CI job covers it.
    /// </summary>
    private static bool TryCreateSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static System.Collections.Generic.IReadOnlyList<FileResult> Plan(TempWorkspace workspace, string inputPath)
    {
        var options = new Options(inputPath, Path.Combine(workspace.Root, "manifest.csv"), workspace.Root, Phase.Plan, null);
        return new FileSplitter(options).Run().ReportRows.OrderBy(r => r.OriginalPath, StringComparer.Ordinal).ToArray();
    }
}
