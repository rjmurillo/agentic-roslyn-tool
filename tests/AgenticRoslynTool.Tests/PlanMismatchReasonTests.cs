using Xunit;

namespace AgenticRoslynTool.Tests;

// A content phase row that disagrees with its plan is skipped with one shared reason. The
// row records this run's values, so the note says what the manifest held and each cause
// has its own note. These tests edit one thing in a reviewed plan and pin the text.
public sealed class PlanMismatchReasonTests
{
    private const string ThreeTypes = "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n\npublic class Baz\n{\n}\n";

    [Fact]
    public void RenameTheManifestDoesNotPlan_SaysWhichSidePlansIt()
    {
        // X.cs holds no type named X, so the plan renames it for Foo. The kept paths
        // agree, so only the rename flag differs. The opposite edit never gets this far:
        // a manifest that claims a rename is checked against the disk first.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("X.cs", ThreeTypes);

        var result = ContentAfterEdit(ws, source, row => row with { GitMove = false });

        Assert.Equal("manifest plans no rename, this run renames the kept file", result.Note);
        AssertNothingWritten(ws, source, "X.cs");
    }

    [Fact]
    public void NewFileOnlyInTheManifest_IsNamed()
    {
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", ThreeTypes);
        var extra = Path.Combine(ws.Root, "Extra.cs");

        var result = ContentAfterEdit(ws, source, row => row with { NewFiles = row.NewFiles.Append(new NewFileResult(extra, "Extra")).ToArray() });

        Assert.Equal($"manifest has {extra}, which this run does not plan", result.Note);
        AssertNothingWritten(ws, source, "Foo.cs");
    }

    [Fact]
    public void NewFileOnlyInThisRun_IsNamed()
    {
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", ThreeTypes);

        var result = ContentAfterEdit(ws, source, row => row with { NewFiles = row.NewFiles.Where(f => f.Type != "Baz").ToArray() });

        Assert.Equal($"this run plans {Path.Combine(ws.Root, "Baz.cs")}, which the manifest lacks", result.Note);
        AssertNothingWritten(ws, source, "Foo.cs");
    }

    [Fact]
    public void NewFilePathListedTwiceInTheManifest_IsNamed()
    {
        // The second entry names a type the source does not hold, so it is never
        // substituted and only the count gives it away.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", ThreeTypes);
        var bar = Path.Combine(ws.Root, "Bar.cs");

        var result = ContentAfterEdit(ws, source, row => row with { NewFiles = row.NewFiles.Append(new NewFileResult(bar, "Ghost")).ToArray() });

        Assert.Equal($"manifest lists {bar} more than once", result.Note);
        AssertNothingWritten(ws, source, "Foo.cs");
    }

    private static FileResult ContentAfterEdit(TempWorkspace ws, string source, Func<FileResult, FileResult> edit)
    {
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var planRows = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.StyleCop);
        Assert.Equal("split", Assert.Single(planRows).Status);
        ManifestWriter.Write(planRows.Select(edit).ToArray(), manifestPath, CollisionNaming.FileFirst);

        var result = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop));
        Assert.Equal("skipped", result.Status);
        Assert.Equal("content phase plan does not match manifest", result.Reason);
        return result;
    }

    private static void AssertNothingWritten(TempWorkspace ws, string source, string onlyFile)
    {
        Assert.Equal(ThreeTypes, File.ReadAllText(source));
        Assert.Equal(new[] { onlyFile }, FileNamingRuns.CsFiles(ws));
    }
}
