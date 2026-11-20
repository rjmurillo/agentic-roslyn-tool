using Xunit;

namespace AgenticRoslynTool.Tests;

// The split itself: an ordinary type moves out, the file-local type stays, and the
// compiler agrees that nothing changed.
public sealed class FileLocalSplitTests : FileLocalTestBase
{
    [Fact]
    public void OrdinaryTypeMovesOut_AndTheFileLocalTypeStays()
    {
        var alpha = Write("Alpha.cs", AlphaWithHelper);

        Run(Phase.Plan, alpha);
        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal(("split", alpha, false), (row.Status, row.KeptPath, row.GitMove));
        Assert.Equal(PathOf("Sibling.cs"), Assert.Single(row.NewFiles).Path);
        Assert.Equal(["Alpha.cs", "Sibling.cs"], CsFiles());
        Assert.Contains("file class AlphaHelper", File.ReadAllText(alpha), StringComparison.Ordinal);
        Assert.DoesNotContain("class Sibling", File.ReadAllText(alpha), StringComparison.Ordinal);
        Assert.DoesNotContain("AlphaHelper", File.ReadAllText(PathOf("Sibling.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeAndAfter_BothCompileWithNoDiagnostics()
    {
        var alpha = Write("Alpha.cs", AlphaWithHelper);
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));

        Run(Phase.Plan, alpha);
        Run(Phase.Content, alpha);

        Assert.Equal(["Alpha.cs", "Sibling.cs"], CsFiles());
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }

    [Fact]
    public void MetadataName_IsTheSameBeforeAndAfterTheContentPhase()
    {
        var alpha = Write("Alpha.cs", AlphaWithHelper);
        var before = FileLocalCompiler.MetadataName(Compile(), "AlphaHelper");

        Run(Phase.Plan, alpha);
        Run(Phase.Content, alpha);

        // The name carries the file's name, so it pins "no rename". The file's text did
        // change, so it also shows the name does not depend on the text.
        Assert.StartsWith("<Alpha>F", before, StringComparison.Ordinal);
        Assert.EndsWith("__AlphaHelper", before, StringComparison.Ordinal);
        Assert.NotEqual(AlphaWithHelper, File.ReadAllText(alpha));
        Assert.Equal(before, FileLocalCompiler.MetadataName(Compile(), "AlphaHelper"));
    }

    [Fact]
    public void RenamingTheFile_WouldChangeTheMetadataName()
    {
        // The reason the tool never renames such a file. Same text, another file name.
        var asAlpha = FileLocalCompiler.Compile([(PathOf("Alpha.cs"), AlphaWithHelper)]);
        var renamed = FileLocalCompiler.Compile([(PathOf("AlphaRenamed.cs"), AlphaWithHelper)]);

        Assert.StartsWith("<AlphaRenamed>F", FileLocalCompiler.MetadataName(renamed, "AlphaHelper"), StringComparison.Ordinal);
        Assert.NotEqual(FileLocalCompiler.MetadataName(asAlpha, "AlphaHelper"), FileLocalCompiler.MetadataName(renamed, "AlphaHelper"));
    }

    [Fact]
    public void SplitRow_SaysTheTypeStayedAndThatSa1402StillFlagsTheFile()
    {
        var alpha = Write("Alpha.cs", AlphaWithHelper);

        var planned = Assert.Single(Run(Phase.Plan, alpha));
        var applied = Assert.Single(Run(Phase.Content, alpha));

        const string Note = "file-local type stayed in this file: AlphaHelper; SA1402 still flags the file";
        Assert.Equal(("split", Note), (planned.Status, planned.Note));
        Assert.Equal(("split", Note), (applied.Status, applied.Note));
        Assert.Contains(Note, File.ReadAllText(ManifestPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFileLocalTypesWrittenTheSameWay_BothStay()
    {
        // Two namespace blocks each declare the same file-local helper, character for
        // character. Both stay in the kept file, so the verifier must expect that text twice.
        var alpha = Write("Alpha.cs",
            "namespace Sample.One\n{\n    internal class Alpha\n    {\n    }\n\n    file class Helper\n    {\n    }\n}\n\n"
            + "namespace Sample.Two\n{\n    internal class Beta\n    {\n    }\n\n    file class Helper\n    {\n    }\n}\n");
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));

        Run(Phase.Plan, alpha);
        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal(("split", "file-local type stayed in this file: Helper; SA1402 still flags the file"), (row.Status, row.Note));
        Assert.Equal("Beta", Assert.Single(row.NewFiles).Type);
        Assert.Equal(2, File.ReadAllText(alpha).Split("file class Helper").Length - 1);
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }

    [Theory]
    [InlineData("file struct Pair\n{\n}\n", "Pair; SA1402 flags the file only where it checks more than classes")]
    [InlineData("file interface IMarker\n{\n}\n\nfile enum Mode\n{\n    Off,\n}\n", "IMarker, Mode; SA1402 flags the file only where it checks more than classes")]
    [InlineData("file record Rec(int Value);\n", "Rec; SA1402 still flags the file")]
    public void Note_PromisesTheWarningOnlyWhereSa1402RaisesItByDefault(string fileLocal, string expected)
    {
        // StyleCop SA1402 checks classes and records by default. In a build it flagged a
        // class beside a file record, and nothing beside a file struct, interface, or enum.
        var alpha = Write("Alpha.cs", "namespace Sample;\n\ninternal class Alpha\n{\n}\n\ninternal class Sibling\n{\n}\n\n" + fileLocal);

        var row = Assert.Single(Run(Phase.Plan, alpha));

        Assert.StartsWith(FileLocalTypes.StayNotePrefix, row.Note, StringComparison.Ordinal);
        Assert.EndsWith("stayed in this file: " + expected, row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKindOfFileLocalType_Stays_WhereverItIsDeclared()
    {
        // The file-local types come first, last, and between the ordinary types.
        const string Source =
            "namespace Sample;\n\nfile record FirstHelper(int Value);\n\ninternal class Kinds\n{\n    public int Total() => new FirstHelper(1).Value + (int)Mode.On;\n}\n\n"
            + "file enum Mode\n{\n    Off,\n    On,\n}\n\ninternal class Mover\n{\n}\n\nfile struct Pair\n{\n}\n\nfile interface IMarker\n{\n}\n\nfile delegate int Compute(int value);\n";
        var kinds = Write("Kinds.cs", Source);
        var before = Compile();
        Assert.Empty(FileLocalCompiler.Diagnostics(before));

        var planned = Assert.Single(Run(Phase.Plan, kinds));
        var row = Assert.Single(Run(Phase.Content, kinds));

        Assert.Equal("file-local types stayed in this file: FirstHelper, Mode, Pair, IMarker, Compute; SA1402 still flags the file", planned.Note);
        Assert.Equal("Mover", Assert.Single(row.NewFiles).Type);
        var after = Compile();
        Assert.Empty(FileLocalCompiler.Diagnostics(after));
        foreach (var name in new[] { "FirstHelper", "Mode", "Pair", "IMarker", "Compute" })
        {
            Assert.StartsWith("<Kinds>F", FileLocalCompiler.MetadataName(after, name), StringComparison.Ordinal);
            Assert.Equal(FileLocalCompiler.MetadataName(before, name), FileLocalCompiler.MetadataName(after, name));
        }
    }
}
