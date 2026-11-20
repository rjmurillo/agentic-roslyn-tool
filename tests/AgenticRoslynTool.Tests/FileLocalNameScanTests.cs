using Xunit;

namespace AgenticRoslynTool.Tests;

// The name scan: a type that would move is refused when any identifier in it equals a
// file-local type's name. Each test compiles what the split would have written.
public sealed class FileLocalNameScanTests : FileLocalTestBase
{
    private const string Header = "namespace Sample;\n\ninternal class Alpha\n{\n}\n\n";

    private const string Helper = "file class AlphaHelper\n{\n}\n";

    [Theory]
    [InlineData("new AlphaHelper()")]
    [InlineData("new @AlphaHelper()")]
    [InlineData("new \\u0041lphaHelper()")]
    [InlineData("typeof(Sample.AlphaHelper)")]
    [InlineData("typeof(global::Sample.AlphaHelper)")]
    [InlineData("nameof(AlphaHelper)")]
    [InlineData("new System.Collections.Generic.List<AlphaHelper>()")]
    public void MovedTypeThatNamesAFileLocalType_IsRefused(string expression)
    {
        var alpha = Write("Alpha.cs", Header + "internal class Caller\n{\n    public object Make() => " + expression + ";\n}\n\n" + Helper);

        AssertRefused(alpha, FileLocalTypes.MovedTypeReason, "Caller names AlphaHelper");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS0", StringComparison.Ordinal));
    }

    [Fact]
    public void AttributeWrittenWithoutItsSuffix_StillNamesTheFileLocalType()
    {
        var alpha = Write("Alpha.cs", Header + "[Mark]\ninternal class Caller\n{\n}\n\nfile class MarkAttribute : System.Attribute\n{\n}\n");

        AssertRefused(alpha, FileLocalTypes.MovedTypeReason, "Caller names MarkAttribute");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS0246 ", StringComparison.Ordinal));
    }

    [Fact]
    public void CrefInADocumentationComment_StillNamesTheFileLocalType()
    {
        // A cref lives in trivia, not in code. The moved comment would stop resolving,
        // which is a warning, and a build break wherever warnings are errors.
        var alpha = Write("Alpha.cs", Header + "/// <summary>Works with <see cref=\"AlphaHelper\"/>.</summary>\ninternal class Caller\n{\n}\n\n" + Helper);

        AssertRefused(alpha, FileLocalTypes.MovedTypeReason, "Caller names AlphaHelper");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS1574 ", StringComparison.Ordinal));
    }

    [Fact]
    public void SameNamedTypeInAnotherNamespace_WouldBeBoundInstead_SoTheInputIsRefused()
    {
        // The file-local Shadow hides Other.Shadow inside Alpha.cs. Outside that file the
        // name means Other.Shadow, so the moved code would compile clean and mean
        // something else. Only a refusal catches this one.
        var other = (Path: PathOf("Other.cs"), Text: "namespace Other;\n\ninternal class Shadow\n{\n}\n");
        Write("Other.cs", other.Text);
        var alpha = Write("Alpha.cs", "using Other;\n\n" + Header
            + "internal class Caller\n{\n    public string Name() => typeof(Shadow).FullName;\n}\n\nfile class Shadow\n{\n}\n");

        AssertRefused(alpha, FileLocalTypes.MovedTypeReason, "Caller names Shadow");

        Assert.Equal("file Sample.Shadow", FileLocalCompiler.BoundType(Compile(), "Caller", "Shadow"));
        var split = CompileUncheckedSplit(alpha, other);
        Assert.Empty(FileLocalCompiler.Diagnostics(split));
        Assert.Equal("Other.Shadow", FileLocalCompiler.BoundType(split, "Caller", "Shadow"));
    }

    [Fact]
    public void TypeThatNamesNoFileLocalType_StillMoves()
    {
        // The scan is by name, so a near miss must not refuse the input.
        var alpha = Write("Alpha.cs", Header + "internal class Caller\n{\n    public string AlphaHelperName => \"AlphaHelper\";\n}\n\n" + Helper);

        Run(Phase.Plan, alpha);
        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal("Caller", Assert.Single(row.NewFiles).Type);
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }
}
