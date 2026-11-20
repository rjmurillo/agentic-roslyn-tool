using Xunit;

namespace AgenticRoslynTool.Tests;

// The convention shapes planned paths, so every phase has to use the same one. The content
// phase refuses a plan made under the other one. The renames phase holds each file to the
// manifest's hash and status, not to its paths, so it cannot see a disagreement; the last
// test pins that limit.
public sealed class FileNamingAgreementTests
{
    private const string MismatchReason = "content phase plan does not match manifest";
    private const string FlagHint = "if --file-naming changed since the plan, run the plan phase again and pass one value to every phase";
    private const string FooWithBar = "namespace N;\n\npublic class Foo<T>\n{\n}\n\npublic class Bar\n{\n}\n";

    [Theory]
    [InlineData("metadata", "stylecop", "Foo`1.cs", "Foo.cs")]
    [InlineData("stylecop", "metadata", "Foo.cs", "Foo`1.cs")]
    public void ContentPhase_RefusesAPlanMadeUnderTheOtherConvention(string planValue, string contentValue, string manifestName, string computedName)
    {
        // Strings, not the enum: the enum is internal and a public test method cannot take it.
        var planNaming = Options.Parse(["--input", "list.txt", "--file-naming", planValue]).FileNaming;
        var contentNaming = Options.Parse(["--input", "list.txt", "--file-naming", contentValue]).FileNaming;

        // Only the moved file's name differs here; the kept path and the git move flag
        // agree. The content phase writes the manifest's paths, so this is the case that
        // slips through unless the other convention's name is recognized.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("A.cs", FileNamingRuns.AWithGenericFoo);
        var listPath = ws.WriteInputList(source);

        var result = Assert.Single(FileNamingRuns.RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), planNaming, contentNaming));

        // The reason is shared with ordinary plan drift, so the note has to name the cause.
        // The skipped row replaces the plan row, so the note is also the only place the
        // manifest's path survives.
        Assert.Equal("skipped", result.Status);
        Assert.Equal(MismatchReason, result.Reason);
        Assert.Equal(
            $"manifest path {Path.Combine(ws.Root, manifestName)} is the --file-naming {planValue} name and this run used {contentValue}, which computes {Path.Combine(ws.Root, computedName)}; run the plan phase again and pass one value to every phase",
            result.Note);
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
        Assert.Equal(MismatchReason, result.Reason);
        Assert.Equal($"manifest keeps {source}, this run keeps {Path.Combine(ws.Root, "Foo`1.cs")}; {FlagHint}", result.Note);
        Assert.Equal(FooWithBar, File.ReadAllText(source));
        Assert.Equal(new[] { "Foo.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void ContentPhase_NamesTheLandedRenameWhenTheFlagIsDroppedAfterRenames()
    {
        // Plan and renames under metadata move Foo.cs to Foo`1.cs. A content run without
        // the flag would keep Foo.cs. The skipped row records that computed path, so the
        // note has to carry the path the manifest held, which is where the file now is.
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
        Assert.Equal(MismatchReason, result.Reason);
        Assert.Equal($"manifest keeps {renamedPath}, this run keeps {source}; {FlagHint}", result.Note);
        Assert.Equal(FooWithBar, File.ReadAllText(renamedPath));
        Assert.Equal(new[] { "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void ContentPhase_StillAppliesAHandEditedFreePath()
    {
        // Only the other convention's own name is refused. A reviewer who retypes an output
        // path is obeyed, as before the flag existed.
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

        Assert.Equal("split", result.Status);
        Assert.Equal(new[] { "A.cs", "Elsewhere.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Contains("class Foo<T>", File.ReadAllText(elsewhere));
    }

    [Fact]
    public void RenamesPhase_FollowsItsOwnConventionAndReplacesThePlan()
    {
        // The renames phase recomputes the plan and its rows replace the manifest. A
        // renames run under another convention is therefore not refused: it rewrites the
        // plan, and the content check compares against the rewritten plan. This pins the
        // manifest replacement, so a change to it is deliberate. It does not pin a rename:
        // no git process starts, because neither convention renames A.cs.
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
