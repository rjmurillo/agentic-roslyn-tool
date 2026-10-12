using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A directory input is scanned again by every phase. After the renames phase, the scan finds
// a renamed file under its kept name, not under the name the plan recorded. The content phase
// must still find that file's plan row, or the split is silently lost at exit code 0.
public sealed class DirectoryInputContentPhaseTests
{
    private const string Mismatched = "namespace N;\n\npublic class Order2 { }\n\npublic class Line { }\n";

    [Fact]
    public void DirectoryInput_SplitsARenamedFile_InTheContentPhase()
    {
        using var workspace = new TempWorkspace();
        var source = Path.Combine(workspace.Root, "src");
        workspace.WriteFile(Path.Combine("src", "Models.cs"), "namespace N;\n\npublic class Models { }\n\npublic class Order { }\n");
        var stuff = workspace.WriteFile(Path.Combine("src", "Stuff.cs"), Mismatched);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var plan = new FileSplitter(new Options(source, manifestPath, workspace.Root, Phase.Plan, null)).Run();
        ManifestWriter.Write(plan.ManifestRows, manifestPath);

        // Stand in for the renames phase, which needs a real repository to run git mv.
        var keptPath = Path.Combine(source, "Order2.cs");
        File.Move(stuff, keptPath);

        var content = new FileSplitter(new Options(source, manifestPath, workspace.Root, Phase.Content, null)).Run();

        var row = Assert.Single(content.ReportRows, r => PathComparison.Comparer.Equals(r.OriginalPath, stuff));
        Assert.Equal("split", row.Status);
        Assert.Equal(keptPath, row.KeptPath);
        Assert.True(row.GitMove);
        Assert.All(content.ReportRows, r => Assert.Equal("split", r.Status));
        Assert.Equal(2, content.ReportRows.Count);
        Assert.Equal(2, content.ManifestRows.Count);

        Assert.Equal("namespace N;\n\npublic class Line { }\n", File.ReadAllText(Path.Combine(source, "Line.cs")));
        Assert.DoesNotContain("Line", File.ReadAllText(keptPath), StringComparison.Ordinal);
    }

    [Fact]
    public void InputNamingBothOriginalAndKeptPath_IsProcessedOnce()
    {
        // A list that joins paths captured before and after the renames phase names one file
        // twice. Both names lead to the same plan row, so the split runs once.
        using var workspace = new TempWorkspace();
        var stuff = workspace.WriteFile("Stuff.cs", Mismatched);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var keptPath = Path.Combine(workspace.Root, "Order2.cs");

        var plan = new FileSplitter(new Options(workspace.WriteInputList(stuff), manifestPath, workspace.Root, Phase.Plan, null)).Run();
        ManifestWriter.Write(plan.ManifestRows, manifestPath);
        File.Move(stuff, keptPath);

        var listPath = workspace.WriteInputList(keptPath, stuff);
        var content = new FileSplitter(new Options(listPath, manifestPath, workspace.Root, Phase.Content, null)).Run();

        var row = Assert.Single(content.ReportRows);
        Assert.Equal("split", row.Status);
        Assert.Equal(stuff, row.OriginalPath);
        Assert.Single(content.ManifestRows);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Line.cs")));
    }

    [Fact]
    public void IndexByInputPath_MapsAUniqueRenamedKeptPathToItsRow()
    {
        var renamed = Row("/r/Stuff.cs", "/r/Order2.cs", gitMove: true);
        var index = SplitPlanner.IndexByInputPath([renamed]);

        Assert.Same(renamed, index["/r/Stuff.cs"]);
        Assert.Same(renamed, index["/r/Order2.cs"]);
    }

    [Fact]
    public void IndexByInputPath_RefusesAKeptPathTwoRowsClaim()
    {
        // Only a hand-edited manifest produces this. Guessing which row the file belongs to
        // could apply the wrong plan, so the kept path maps to neither.
        var first = Row("/r/A.cs", "/r/Order2.cs", gitMove: true);
        var second = Row("/r/B.cs", "/r/Order2.cs", gitMove: true);
        var index = SplitPlanner.IndexByInputPath([first, second]);

        Assert.False(index.ContainsKey("/r/Order2.cs"));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void IndexByInputPath_PrefersTheRowWhoseOriginalPathMatches()
    {
        // A kept path that is also some row's original path belongs to that row. The original
        // path is what the plan phase saw on disk, so it is the stronger claim.
        var owner = Row("/r/Order2.cs", "/r/Order2.cs", gitMove: false);
        var renamed = Row("/r/Stuff.cs", "/r/Order2.cs", gitMove: true);
        var index = SplitPlanner.IndexByInputPath([owner, renamed]);

        Assert.Same(owner, index["/r/Order2.cs"]);
        Assert.Same(renamed, index["/r/Stuff.cs"]);
    }

    [Fact]
    public void IndexByInputPath_IgnoresTheKeptPathOfARowWithNoRename()
    {
        // Without a git move the file never left its original path, so a kept path that
        // differs is not a place the file can be. Only a hand-edited manifest says otherwise.
        var unmoved = Row("/r/Stuff.cs", "/r/Order2.cs", gitMove: false);
        var index = SplitPlanner.IndexByInputPath([unmoved]);

        Assert.False(index.ContainsKey("/r/Order2.cs"));
        Assert.Single(index);
    }

    private static FileResult Row(string originalPath, string keptPath, bool gitMove) =>
        FileResult.Split(originalPath, keptPath, gitMove, null, [new NewFileResult("/r/Line.cs", "Line")]);
}
