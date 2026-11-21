using Xunit;

namespace AgenticRoslynTool.Tests;

// Refusals that guard the kept file and the build around it: a directive every output
// repeats, and a directive only one output keeps. Both break an ordinary input too. An
// input with a file-local type is refused, because its file must not change meaning.
public sealed class FileLocalKeptFileRefusalTests : FileLocalTestBase
{
    private const string Alpha = "namespace Sample;\n\ninternal class Alpha\n{\n}\n\n";

    private const string Sibling = "internal class Sibling\n{\n}\n\n";

    [Fact]
    public void GlobalUsingAlias_WouldBeDeclaredInTwoFiles_SoTheInputIsRefused()
    {
        // Every output repeats every using directive. A global alias applies to the whole
        // project, so the second copy does not compile.
        var alpha = Write("Alpha.cs", "global using Clock = System.DateTime;\n\n" + Alpha + Sibling + "file class AlphaHelper\n{\n}\n");

        AssertRefused(alpha, FileLocalDirectives.GlobalUsingReason, null);

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS1537 ", StringComparison.Ordinal));
    }

    [Fact]
    public void LineDirectiveAboveAMovedType_WouldLeaveTheKeptFile_SoTheInputIsRefused()
    {
        // The directive maps every line below it, the file-local type included. It is
        // attached to Sibling, so it would leave with Sibling. No diagnostic says so.
        var alpha = Write("Alpha.cs", Alpha + "#line 100 \"Mapped.cs\"\n" + Sibling + "file class AlphaHelper\n{\n}\n");

        AssertRefused(alpha, FileLocalDirectives.DirectiveStateReason, "#line directive at line 7");

        Assert.Equal("Mapped.cs", FileLocalCompiler.MappedFile(Compile(), "AlphaHelper"));
        var split = CompileUncheckedSplit(alpha);
        Assert.Empty(FileLocalCompiler.Diagnostics(split));
        Assert.Equal("Alpha.cs", FileLocalCompiler.MappedFile(split, "AlphaHelper"));
    }

    [Theory]
    [InlineData("#nullable enable", "    public string? Name() => null;", "CS8632 ", "#nullable")]
    [InlineData("#pragma warning disable CS0168", "    public void Run()\n    {\n        int unused;\n    }", "CS0168 ", "#pragma")]
    public void StateDirectiveAboveAMovedType_WouldLeaveTheKeptFile_SoTheInputIsRefused(string directive, string member, string diagnostic, string kind)
    {
        // The file-local type relies on the directive above Sibling. Without it the kept
        // file raises a warning it did not raise before.
        var alpha = Write("Alpha.cs", Alpha + directive + "\n" + Sibling + "file class AlphaHelper\n{\n" + member + "\n}\n");

        AssertRefused(alpha, FileLocalDirectives.DirectiveStateReason, kind + " directive at line 7");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void StateDirectiveAboveTheNamespace_ReachesEveryOutput_SoTheInputSplits()
    {
        // A directive at the top of the file belongs to no type. Every output repeats it,
        // so nothing is lost and the refusal does not apply.
        var alpha = Write("Alpha.cs", "#nullable enable\n" + Alpha + "internal class Sibling\n{\n    public string? Name() => null;\n}\n\nfile class AlphaHelper\n{\n}\n");

        Run(Phase.Plan, alpha);
        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal("Sibling", Assert.Single(row.NewFiles).Type);
        Assert.Contains("#nullable enable", File.ReadAllText(PathOf("Sibling.cs")), StringComparison.Ordinal);
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }
}
