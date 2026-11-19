using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A backtick is an unusual character in a path. This test drives one through the real
// git mv of the renames phase, in a temporary repository.
public sealed class MetadataRenameTests
{
    [Fact]
    public void Metadata_RenamesAGenericKeptFileThroughGitMvThenSplits()
    {
        // Foo.cs is the wrong name for Foo<T> under metadata, so the kept file is renamed.
        // This runs the real git mv, which is where a backtick in a path would break.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", "namespace N;\n\npublic class Foo<T>\n{\n}\n\npublic class Bar\n{\n}\n");
        var before = File.ReadAllBytes(source);
        var listPath = Path.Combine(Path.GetDirectoryName(ws.Root)!, Path.GetFileName(ws.Root) + ".inputs.txt");
        var manifestPath = Path.Combine(Path.GetDirectoryName(ws.Root)!, Path.GetFileName(ws.Root) + ".manifest.csv");
        try
        {
            File.WriteAllLines(listPath, [source]);
            FileNamingRuns.Git(ws.Root, "init", "--quiet");
            FileNamingRuns.Git(ws.Root, "add", "Foo.cs");
            var kept = Path.Combine(ws.Root, "Foo`1.cs");

            var plan = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata));
            Assert.Equal("split", plan.Status);
            Assert.True(plan.GitMove);
            Assert.Equal(kept, plan.KeptPath);
            Assert.Equal(new[] { "Foo.cs" }, FileNamingRuns.CsFiles(ws));

            var renamed = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.Metadata));
            Assert.Equal("split", renamed.Status);
            Assert.Equal(new[] { "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
            Assert.Equal(before, File.ReadAllBytes(kept));

            // The index follows the move, which only git mv does. Nothing is committed in
            // this fixture, so git reports a moved staged file rather than a rename.
            Assert.Equal("Foo`1.cs", FileNamingRuns.Git(ws.Root, "-c", "core.quotePath=false", "ls-files").Trim());

            var content = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata));
            Assert.Equal("split", content.Status);
            Assert.Equal(new[] { "Bar.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
            Assert.Contains("class Foo<T>", File.ReadAllText(kept));
            Assert.DoesNotContain("class Bar", File.ReadAllText(kept));
        }
        finally
        {
            File.Delete(listPath);
            File.Delete(manifestPath);
        }
    }
}
