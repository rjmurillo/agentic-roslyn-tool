using Xunit;

namespace AgenticRoslynTool.Tests;

// How a split that leaves a file-local type behind meets the rest of the workflow: the
// content hash, a retry, an edited manifest, both naming flags, and the report file.
public sealed class FileLocalPhaseTests : FileLocalTestBase
{
    private const string MismatchReason = "content phase plan does not match manifest";

    [Fact]
    public void InputEditedAfterThePlan_IsStillRefusedAsAStalePlan()
    {
        var alpha = Write("Alpha.cs", AlphaWithHelper);
        Run(Phase.Plan, alpha);
        File.AppendAllText(alpha, "\n// edited after the plan\n");

        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("stale plan:", row.Reason, StringComparison.Ordinal);
        Assert.Equal(["Alpha.cs"], CsFiles());
    }

    [Fact]
    public void ContentRetry_ReportsTheRowAsAlreadyApplied()
    {
        // After the first run the kept file still declares two types. The retry check
        // must still read it as applied work.
        var alpha = Write("Alpha.cs", AlphaWithHelper);
        Run(Phase.Plan, alpha);
        Run(Phase.Content, alpha);
        var afterFirstRun = File.ReadAllText(alpha);

        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal(("skipped", FileSplitter.AlreadyAppliedReason), (row.Status, row.Reason));
        Assert.Equal(afterFirstRun, File.ReadAllText(alpha));
    }

    [Fact]
    public void ManifestEditedToPlanARename_IsRefused_EvenWhenTheFileWasRenamedByHand()
    {
        // The plan never asks for a rename. Someone edits the row to ask for one and
        // renames the file to match. The content phase must not accept that.
        var alpha = Write("Alpha.cs", AlphaWithHelper);
        var planned = Assert.Single(Run(Phase.Plan, alpha));
        var renamed = PathOf("AlphaRenamed.cs");
        ManifestWriter.Write([planned with { GitMove = true, KeptPath = renamed }], ManifestPath, CollisionNaming.FileFirst);
        File.Move(alpha, renamed);

        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal(("skipped", MismatchReason), (row.Status, row.Reason));
        Assert.Equal(["AlphaRenamed.cs"], CsFiles());
        Assert.Equal(AlphaWithHelper, File.ReadAllText(renamed));
    }

    [Fact]
    public void MovedGenericType_TakesItsMetadataName_AndTheKeptFileKeepsItsOwn()
    {
        Naming = FileNamingConvention.Metadata;
        var alpha = Write("Alpha.cs", AlphaWithHelper + "\ninternal class Box<T>\n{\n}\n");

        Run(Phase.Plan, alpha);
        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal(("split", alpha, false), (row.Status, row.KeptPath, row.GitMove));
        Assert.Equal(["Alpha.cs", "Box`1.cs", "Sibling.cs"], CsFiles());
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }

    [Fact]
    public void PlanMadeUnderOneFileNaming_IsRefusedUnderTheOther()
    {
        Naming = FileNamingConvention.Metadata;
        var alpha = Write("Alpha.cs", AlphaWithHelper + "\ninternal class Box<T>\n{\n}\n");
        Run(Phase.Plan, alpha);
        Naming = FileNamingConvention.StyleCop;

        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal(("skipped", MismatchReason), (row.Status, row.Reason));
        Assert.Equal(["Alpha.cs"], CsFiles());
    }

    [Theory]
    [InlineData("type-first", "Shared.A.cs", "Shared.B.cs")]
    [InlineData("file-first", "A.Shared.cs", "B.Shared.cs")]
    public void CollisionName_AndTheStayNote_ShareOneRow(string value, string fromA, string fromB)
    {
        // Strings, not the enum: the enum is internal and a public test method cannot take it.
        var naming = Options.Parse(["--input", "list.txt", "--collision-naming", value]).CollisionNaming!.Value;
        var (list, a) = WriteCollidingPair();

        var plan = FileNamingRuns.RunPhase(Workspace, list, ManifestPath, Phase.Plan, Naming, naming);
        var content = FileNamingRuns.RunPhase(Workspace, list, ManifestPath, Phase.Content, Naming, naming);

        var note = plan.Single(r => r.OriginalPath == a).Note!;
        Assert.StartsWith("file-local type stayed in this file: AHelper; SA1402 still flags the file | resolved output path collision: ", note, StringComparison.Ordinal);
        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "A.cs", "B.cs", fromA, fromB }.Order(StringComparer.Ordinal), CsFiles());
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }

    [Fact]
    public void CollisionName_PlannedUnderOneValue_IsRefusedWhenTheManifestSaysTheOther()
    {
        // The stay note shares the row with the collision note. The per-row check reads
        // that note, and must still find the collision name in it.
        var (list, _) = WriteCollidingPair();
        FileNamingRuns.RunPhase(Workspace, list, ManifestPath, Phase.Plan, Naming, CollisionNaming.TypeFirst, recordAs: CollisionNaming.FileFirst);

        var content = FileNamingRuns.RunPhase(Workspace, list, ManifestPath, Phase.Content, Naming);

        Assert.All(content, r => Assert.Equal(("skipped", MismatchReason), (r.Status, r.Reason)));
        Assert.Equal(["A.cs", "B.cs"], CsFiles());
    }

    [Fact]
    public void RealRun_WritesTheStayNoteToTheManifestAndToTheReport()
    {
        // Which file a run writes is decided in Program, so this runs the executable.
        var alpha = Write("Alpha.cs", AlphaWithHelper);
        var report = PathOf("run.report.csv");

        var plan = CliRunner.Run("split-types", "--input", alpha, "--manifest", ManifestPath, "--repo-root", Workspace.Root);
        var manifest = File.ReadAllText(ManifestPath);
        var content = CliRunner.Run("split-types", "--input", alpha, "--manifest", ManifestPath, "--repo-root", Workspace.Root, "--phase", "content", "--report", report);

        const string Note = "file-local type stayed in this file: AlphaHelper; SA1402 still flags the file";
        Assert.Equal((0, 0), (plan.ExitCode, content.ExitCode));
        Assert.Contains(Note, manifest, StringComparison.Ordinal);
        Assert.Contains(Note, File.ReadAllText(report), StringComparison.Ordinal);
        Assert.Equal(manifest, File.ReadAllText(ManifestPath));
        Assert.Equal(["Alpha.cs", "Sibling.cs"], CsFiles());
    }

    [Fact]
    public void RequiredHeaderThatIsADirective_IsNotMistakenForALostOne()
    {
        // The verifier compares each output's directives with the input's. A header the
        // caller asked for is added to every output, so it must not count as a difference.
        var alpha = Write("Alpha.cs", AlphaWithHelper);
        FileResult[] RunWithHeader(Phase phase)
        {
            var options = new Options(Workspace.WriteInputList(alpha), ManifestPath, Workspace.Root, phase, RequiredHeader: "#pragma warning disable CS1591", FileNaming: Naming);
            var rows = new FileSplitter(options).Run(out var naming);
            if (phase == Phase.Plan)
            {
                ManifestWriter.Write(rows, ManifestPath, naming);
            }

            return rows.ToArray();
        }

        RunWithHeader(Phase.Plan);
        var row = Assert.Single(RunWithHeader(Phase.Content));

        Assert.Equal(("split", null), (row.Status, row.Reason));
        Assert.StartsWith("#pragma warning disable CS1591", File.ReadAllText(PathOf("Sibling.cs")), StringComparison.Ordinal);
    }

    // A.cs and B.cs each hold a file-local type and a Shared type, so both plan Shared.cs.
    private (string ListPath, string APath) WriteCollidingPair()
    {
        var a = Write("A.cs", "namespace Sample.One;\n\ninternal class A\n{\n    public static string HelperName() => typeof(AHelper).Name;\n}\n\nfile class AHelper\n{\n}\n\ninternal class Shared\n{\n}\n");
        var b = Write("B.cs", "namespace Sample.Two;\n\ninternal class B\n{\n    public static string HelperName() => typeof(BHelper).Name;\n}\n\nfile class BHelper\n{\n}\n\ninternal class Shared\n{\n}\n");
        return (Workspace.WriteInputList(a, b), a);
    }
}
