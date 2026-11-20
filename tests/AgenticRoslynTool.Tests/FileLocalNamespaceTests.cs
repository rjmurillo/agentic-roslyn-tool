using Xunit;

namespace AgenticRoslynTool.Tests;

// File-local types in files with more than one namespace: nested blocks and sibling
// blocks. The tool places types at any namespace depth, so each of these splits.
public sealed class FileLocalNamespaceTests : FileLocalTestBase
{
    private const string Outer = "namespace Sample\n{\n    internal class Alpha\n    {\n    }\n\n    internal class Second\n    {\n    }\n\n";

    [Fact]
    public void FileLocalTypeInANestedNamespace_StaysAndIsNotCopied()
    {
        var alpha = Write("Alpha.cs", Outer + "    namespace Inner\n    {\n        file class Hidden\n        {\n        }\n    }\n}\n");
        var before = Compile();
        Assert.Empty(FileLocalCompiler.Diagnostics(before));

        Run(Phase.Plan, alpha);
        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal(("split", alpha, false), (row.Status, row.KeptPath, row.GitMove));
        Assert.Equal("Second", Assert.Single(row.NewFiles).Type);
        Assert.DoesNotContain("Hidden", File.ReadAllText(PathOf("Second.cs")), StringComparison.Ordinal);
        var after = Compile();
        Assert.Empty(FileLocalCompiler.Diagnostics(after));
        Assert.Equal(1, FileLocalCompiler.CountDeclared(after, "Hidden"));
        Assert.Equal(FileLocalCompiler.MetadataName(before, "Hidden"), FileLocalCompiler.MetadataName(after, "Hidden"));
    }

    [Fact]
    public void OrdinaryTypeInANestedNamespace_MovesOut_WhenItNamesNoFileLocalType()
    {
        var alpha = Write("Alpha.cs", Outer + "    file class AlphaHelper\n    {\n    }\n\n    namespace Inner\n    {\n        internal class Deep\n        {\n        }\n    }\n}\n");
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));

        Run(Phase.Plan, alpha);
        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal(["Deep", "Second"], row.NewFiles.Select(f => f.Type).Order(StringComparer.Ordinal));
        var after = Compile();
        Assert.Empty(FileLocalCompiler.Diagnostics(after));
        Assert.Equal(1, FileLocalCompiler.CountDeclared(after, "Deep"));
    }

    [Fact]
    public void OrdinaryTypeInANestedNamespace_ThatNamesAFileLocalType_RefusesTheInput()
    {
        // The scan reads a type at any depth. Deep reaches the helper through its outer
        // namespace, so Deep can never move.
        var alpha = Write("Alpha.cs", Outer + "    file class AlphaHelper\n    {\n    }\n\n    namespace Inner\n    {\n        internal class Deep\n        {\n            public object Make() => new AlphaHelper();\n        }\n    }\n}\n");

        AssertRefused(alpha, FileLocalTypes.MovedTypeReason, "Deep names AlphaHelper");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS0246 ", StringComparison.Ordinal));
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
}
