using Xunit;

namespace AgenticRoslynTool.Tests;

// The convention shapes planned paths, so every phase has to use the same one. The content
// phase refuses a plan made under the other one. The renames phase runs only the renames
// the plan holds, so under another value it moves nothing the plan did not ask for. It can
// still leave out a planned rename, and the last test pins that limit.
public sealed class FileNamingAgreementTests
{
    private const string MismatchReason = "content phase plan does not match manifest";
    private const string PlanAgain = "run the content phase again with the value the plan was made with, or plan again and pass one value to every phase";
    private const string FlagHint = "if a planned rename has not landed, run the renames phase; if --file-naming changed since the plan, " + PlanAgain;
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

        // The reason is shared with ordinary plan drift, so the note has to name the cause
        // and both paths.
        Assert.Equal("skipped", result.Status);
        Assert.Equal(MismatchReason, result.Reason);
        Assert.Equal(
            $"manifest path {Path.Combine(ws.Root, manifestName)} is the --file-naming {planValue} name and this run used {contentValue}, which computes {Path.Combine(ws.Root, computedName)}; {PlanAgain}",
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
            manifestPath, CollisionNaming.FileFirst);

        var result = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop));

        Assert.Equal("split", result.Status);
        Assert.Equal(new[] { "A.cs", "Elsewhere.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Contains("class Foo<T>", File.ReadAllText(elsewhere));
    }

    [Fact]
    public void RenamesPhase_UnderAnotherConvention_ReportsItsOwnNamesAndLeavesThePlan()
    {
        // The renames phase recomputes the plan under the flag it was given and is not
        // held to planned paths. Its rows go to the run report, never to the manifest, so
        // the reviewed plan survives and a content run under the plan's value applies it.
        // No git process starts, because neither convention renames A.cs.
        using var ws = new TempWorkspace();
        var listPath = ws.WriteInputList(ws.WriteFile("A.cs", FileNamingRuns.AWithGenericFoo));
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var planned = Path.Combine(ws.Root, "Foo`1.cs");

        var plan = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata));
        Assert.Equal(planned, Assert.Single(plan.NewFiles).Path);

        var renames = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.StyleCop));

        Assert.Equal("split", renames.Status);
        Assert.Equal(Path.Combine(ws.Root, "Foo.cs"), Assert.Single(renames.NewFiles).Path);
        Assert.Equal(planned, Assert.Single(Assert.Single(ManifestWriter.Read(manifestPath)).NewFiles).Path);

        var content = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata));

        Assert.Equal("split", content.Status);
        Assert.Equal(new[] { "A.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void RenamesPhase_UnderAnotherConvention_DoesNotRenameAFileThePlanLeftInPlace()
    {
        // The plan keeps Foo.cs. A renames run with metadata would move it to Foo`1.cs, and
        // the content phase reads the path the manifest kept. So the renames phase drops a
        // rename the plan did not ask for, and the reviewed plan still applies.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", FooWithBar);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var plan = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.StyleCop));
        Assert.False(plan.GitMove);

        var renames = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.Metadata));

        Assert.Equal(("split", false, source), (renames.Status, renames.GitMove, renames.KeptPath));
        Assert.Equal($"rename to {Path.Combine(ws.Root, "Foo`1.cs")} is not in the plan; kept original file name", renames.Note);
        Assert.Equal(new[] { "Foo.cs" }, FileNamingRuns.CsFiles(ws));

        var content = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop));

        Assert.Equal("split", content.Status);
        Assert.Equal(new[] { "Bar.cs", "Foo.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Theory]
    [InlineData("stylecop", "metadata", "Foo.cs", "Foo`1.cs")]
    [InlineData("metadata", "stylecop", "Foo`1.cs", "Foo.cs")]
    public void RenamesPhase_UnderAnotherConvention_DoesNotRenameToAnotherTarget(string planValue, string otherValue, string plannedName, string otherName)
    {
        // X.cs is named for neither type, so both conventions rename it, to different
        // names. A renames run under the other value must not move it to a name the plan
        // never held. The file stays put, and a renames run with the plan's value recovers.
        var planNaming = Options.Parse(["--input", "list.txt", "--file-naming", planValue]).FileNaming;
        var otherNaming = Options.Parse(["--input", "list.txt", "--file-naming", otherValue]).FileNaming;
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("X.cs", FooWithBar);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        FileNamingRuns.Git(ws.Root, "init", "--quiet");
        FileNamingRuns.Git(ws.Root, "add", "X.cs");
        var plan = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, planNaming));
        Assert.Equal((true, Path.Combine(ws.Root, plannedName)), (plan.GitMove, plan.KeptPath));

        var renames = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, otherNaming));

        Assert.Equal(("split", false, source), (renames.Status, renames.GitMove, renames.KeptPath));
        Assert.Equal($"rename to {Path.Combine(ws.Root, otherName)} is not in the plan; kept original file name", renames.Note);
        Assert.Equal(new[] { "X.cs" }, FileNamingRuns.CsFiles(ws));

        var content = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, planNaming));

        Assert.Equal("failed", content.Status);
        Assert.Contains("Run --phase renames", content.Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "X.cs" }, FileNamingRuns.CsFiles(ws));

        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, planNaming);
        var retry = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, planNaming));

        Assert.Equal("split", retry.Status);
        Assert.Equal(new[] { "Bar.cs", plannedName }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void RenamesPhase_UnderAnotherConvention_DoesNotMoveALandedRenameBack()
    {
        // After the planned rename has landed, a directory run finds the file at its kept
        // path. Under the other value the phase would rename it back. The plan holds no
        // such rename, so the file stays where the plan put it.
        using var ws = new TempWorkspace();
        ws.WriteFile("Foo.cs", FooWithBar);
        var manifestPath = Path.Combine(Path.GetDirectoryName(ws.Root)!, Path.GetFileName(ws.Root) + ".m.csv");
        try
        {
            FileNamingRuns.Git(ws.Root, "init", "--quiet");
            FileNamingRuns.Git(ws.Root, "add", "Foo.cs");
            FileNamingRuns.RunPhase(ws, ws.Root, manifestPath, Phase.Plan, FileNamingConvention.Metadata);
            FileNamingRuns.RunPhase(ws, ws.Root, manifestPath, Phase.Renames, FileNamingConvention.Metadata);
            Assert.Equal(new[] { "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));

            var again = Assert.Single(FileNamingRuns.RunPhase(ws, ws.Root, manifestPath, Phase.Renames, FileNamingConvention.StyleCop));

            Assert.False(again.GitMove);
            Assert.Equal(new[] { "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));

            var content = Assert.Single(FileNamingRuns.RunPhase(ws, ws.Root, manifestPath, Phase.Content, FileNamingConvention.Metadata));

            Assert.Equal("split", content.Status);
            Assert.Equal(new[] { "Bar.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
        }
        finally
        {
            File.Delete(manifestPath);
            File.Delete(Path.ChangeExtension(manifestPath, ".report.csv"));
        }
    }

    [Fact]
    public void RenamesPhase_UnderAnotherConvention_CanLeaveOutAPlannedRename()
    {
        // The known limit, pinned so a change to it is deliberate. The plan renames Foo.cs
        // to Foo`1.cs. A renames run without the flag plans no rename and reports success.
        // The content run then asks for the renamed file and writes nothing. Running the
        // renames phase again with the plan's value recovers.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", FooWithBar);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        FileNamingRuns.Git(ws.Root, "init", "--quiet");
        FileNamingRuns.Git(ws.Root, "add", "Foo.cs");
        var plan = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata));
        Assert.True(plan.GitMove);

        var renames = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.StyleCop));

        Assert.Equal(("split", false), (renames.Status, renames.GitMove));
        Assert.Equal(new[] { "Foo.cs" }, FileNamingRuns.CsFiles(ws));

        var content = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata));

        Assert.Equal("failed", content.Status);
        Assert.Contains("Run --phase renames", content.Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "Foo.cs" }, FileNamingRuns.CsFiles(ws));

        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.Metadata);
        var retry = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata));

        Assert.Equal("split", retry.Status);
        Assert.Equal(new[] { "Bar.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
    }
}
