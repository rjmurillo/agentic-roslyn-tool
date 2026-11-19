using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The convention shapes planned paths, so every phase has to use the same one. The content
// phase refuses a plan it could not have made. The renames phase replays no manifest, so it
// cannot see a disagreement; the last test pins that limit.
public sealed class FileNamingAgreementTests
{
    private const string FooWithBar = "namespace N;\n\npublic class Foo<T>\n{\n}\n\npublic class Bar\n{\n}\n";

    [Theory]
    [InlineData(FileNamingConvention.Metadata, FileNamingConvention.StyleCop, "Foo`1.cs", "Foo.cs")]
    [InlineData(FileNamingConvention.StyleCop, FileNamingConvention.Metadata, "Foo.cs", "Foo`1.cs")]
    public void ContentPhase_RefusesAPlanMadeUnderTheOtherConvention(object planNaming, object contentNaming, string manifestName, string computedName)
    {
        // Only the moved file's name differs here; the kept path and the git move flag
        // agree. The content phase substitutes manifest paths before it compares, so this
        // is the case that slips through unless computed paths are compared as well.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("A.cs", FileNamingRuns.AWithGenericFoo);
        var listPath = ws.WriteInputList(source);

        var result = Assert.Single(FileNamingRuns.RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), (FileNamingConvention)planNaming, (FileNamingConvention)contentNaming));

        // The reason names both paths. The skipped row replaces the plan row, so the reason
        // is the only place the disagreeing manifest path survives.
        Assert.Equal("skipped", result.Status);
        Assert.Equal(
            $"content phase plan does not match manifest: manifest has {Path.Combine(ws.Root, manifestName)}, this run computes {Path.Combine(ws.Root, computedName)}",
            result.Reason);
        Assert.Equal(FileNamingRuns.AWithGenericFoo, File.ReadAllText(source));
        Assert.Equal(new[] { "A.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void ContentPhase_NamesBothKeptPathsWhenTheConventionMovesTheKeptFile()
    {
        // Foo.cs is the right name for Foo<T> under stylecop and the wrong one under
        // metadata, so the two conventions disagree on the kept path, not on a new file.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", FooWithBar);
        var listPath = ws.WriteInputList(source);

        var result = Assert.Single(FileNamingRuns.RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), FileNamingConvention.StyleCop, FileNamingConvention.Metadata));

        Assert.Equal("skipped", result.Status);
        Assert.Equal(
            $"content phase plan does not match manifest: manifest keeps {source}, this run keeps {Path.Combine(ws.Root, "Foo`1.cs")}",
            result.Reason);
        Assert.Equal(FooWithBar, File.ReadAllText(source));
        Assert.Equal(new[] { "Foo.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void ContentPhase_NamesTheLandedRenameWhenTheFlagIsDroppedAfterRenames()
    {
        // Plan and renames under metadata move Foo.cs to Foo`1.cs. A content run without
        // the flag would keep Foo.cs. The skipped row records that computed path, so the
        // reason has to carry the path the manifest held, which is where the file now is.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", FooWithBar);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var renamedPath = Path.Combine(ws.Root, "Foo`1.cs");
        FileNamingRuns.Git(ws.Root, "init", "--quiet");
        FileNamingRuns.Git(ws.Root, "add", "Foo.cs");
        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata);
        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.Metadata);

        var result = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop));

        Assert.Equal("skipped", result.Status);
        Assert.Equal(
            $"content phase plan does not match manifest: manifest keeps {renamedPath}, this run keeps {source}",
            result.Reason);
        Assert.Equal(FooWithBar, File.ReadAllText(renamedPath));
        Assert.Equal(new[] { "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void ContentPhase_RefusesAManifestPathNoPlanCouldHoldEvenWhenItIsFree()
    {
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("A.cs", FileNamingRuns.AWithGenericFoo);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var planRows = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.StyleCop);
        var elsewhere = Path.Combine(ws.Root, "Elsewhere.cs");
        ManifestWriter.Write(
            planRows.Select(r => r with { NewFiles = r.NewFiles.Select(f => f with { Path = elsewhere }).ToArray() }).ToArray(),
            manifestPath);

        var result = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop));

        Assert.Equal("skipped", result.Status);
        Assert.Equal(
            $"content phase plan does not match manifest: manifest has {elsewhere}, this run computes {Path.Combine(ws.Root, "Foo.cs")}",
            result.Reason);
        Assert.Equal(new[] { "A.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void RenamesPhase_FollowsItsOwnConventionAndReplacesThePlan()
    {
        // The renames phase recomputes the plan and its rows replace the manifest. A
        // renames run under another convention is therefore not refused: it rewrites the
        // plan, and the content check compares against the rewritten plan. This pins that
        // known limit so a change to it is deliberate. No git process starts, because
        // neither convention renames A.cs.
        using var ws = new TempWorkspace();
        var listPath = ws.WriteInputList(ws.WriteFile("A.cs", FileNamingRuns.AWithGenericFoo));
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata));
        Assert.Equal(Path.Combine(ws.Root, "Foo`1.cs"), Assert.Single(plan.NewFiles).Path);

        var renames = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.StyleCop));

        Assert.Equal("split", renames.Status);
        Assert.Equal(Path.Combine(ws.Root, "Foo.cs"), Assert.Single(renames.NewFiles).Path);
        Assert.Equal(Path.Combine(ws.Root, "Foo.cs"), Assert.Single(Assert.Single(ManifestWriter.Read(manifestPath)).NewFiles).Path);
    }
}
