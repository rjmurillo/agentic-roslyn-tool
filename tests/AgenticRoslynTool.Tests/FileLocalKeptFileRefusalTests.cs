using Xunit;

namespace AgenticRoslynTool.Tests;

// Refusals that guard what each file means after a split: a directive every output
// repeats, and a directive only some outputs keep. Both break an ordinary input too. An
// input with a file-local type is refused, because the split must not change meaning.
public sealed class FileLocalKeptFileRefusalTests : FileLocalTestBase
{
    private const string Alpha = "namespace Sample;\n\ninternal class Alpha\n{\n}\n\n";

    private const string Sibling = "internal class Sibling\n{\n}\n\n";

    private const string Nullable = "#nullable enable\n";

    private const string Name = "    public string? Name() => null;\n";

    [Fact]
    public void GlobalUsingAlias_WouldBeDeclaredInTwoFiles_SoTheInputIsRefused()
    {
        // Every output repeats every using directive. A global alias applies to the whole
        // project, so the second copy does not compile.
        var alpha = Write("Alpha.cs", "global using Clock = System.DateTime;\n\n" + Alpha + Sibling + "file class AlphaHelper\n{\n}\n");

        AssertRefused(alpha, FileLocalDirectives.GlobalUsingReason, null);

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS1537 ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("#line 100 \"Mapped.cs\"")]
    [InlineData("#line (100, 1) - (100, 10) \"Mapped.cs\"")]
    public void LineDirectiveAboveAMovedType_WouldLeaveTheKeptFile_SoTheInputIsRefused(string directive)
    {
        // The directive maps every line below it, the file-local type included. It is
        // attached to Sibling, so it would leave with Sibling. No diagnostic says so.
        var alpha = Write("Alpha.cs", Alpha + directive + "\n" + Sibling + "file class AlphaHelper\n{\n}\n");

        AssertRefused(alpha, FileLocalDirectives.DirectiveStateReason, "#line directive at line 7");

        Assert.Equal("Mapped.cs", FileLocalCompiler.MappedFile(Compile(), "AlphaHelper"));
        var split = CompileUncheckedSplit(alpha);
        Assert.Empty(FileLocalCompiler.Diagnostics(split));
        Assert.Equal("Alpha.cs", FileLocalCompiler.MappedFile(split, "AlphaHelper"));
    }

    [Theory]
    [InlineData(Alpha + Nullable + Sibling + "file class AlphaHelper\n{\n" + Name + "}\n", 7)]
    [InlineData(Alpha + "internal class Sibling\n{\n" + Nullable + "}\n\nfile class AlphaHelper\n{\n" + Name + "}\n", 9)]
    [InlineData(Alpha + Nullable + "file class AlphaHelper\n{\n}\n\ninternal class Sibling\n{\n" + Name + "}\n", 7)]
    [InlineData(Nullable + "[assembly: System.CLSCompliant(false)]\n\n" + Alpha + "internal class Sibling\n{\n" + Name + "}\n\nfile class AlphaHelper\n{\n}\n", 1)]
    public void NullableDirective_WhereverASplitWouldDropIt_RefusesTheInput(string source, int line)
    {
        // Above a moved type, inside one, above the file-local type, and on an assembly
        // attribute. In each place some output loses the directive, and a type below it
        // raises a warning it did not raise before.
        var alpha = Write("Alpha.cs", source);

        AssertRefused(alpha, FileLocalDirectives.DirectiveStateReason, "#nullable directive at line " + line);

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS8632 ", StringComparison.Ordinal));
    }

    [Fact]
    public void PragmaAboveAMovedType_WouldLeaveTheKeptFile_SoTheInputIsRefused()
    {
        var alpha = Write("Alpha.cs", Alpha + "#pragma warning disable CS0168\n" + Sibling + "file class AlphaHelper\n{\n    public void Run()\n    {\n        int unused;\n    }\n}\n");

        AssertRefused(alpha, FileLocalDirectives.DirectiveStateReason, "#pragma directive at line 7");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS0168 ", StringComparison.Ordinal));
    }

    [Fact]
    public void DefineOnAnAssemblyAttribute_WouldLeaveEveryMovedFile_SoTheInputIsRefused()
    {
        // Only the kept file keeps assembly attributes. The symbol goes with them, so the
        // member inside #if would vanish from the moved type.
        var alpha = Write("Alpha.cs", "#define EXTRA\n[assembly: System.CLSCompliant(false)]\n\nnamespace Sample;\n\ninternal class Alpha\n{\n    public int Read() => new Sibling().Value;\n}\n\n"
            + "internal class Sibling\n{\n#if EXTRA\n    public int Value => 1;\n#endif\n}\n\nfile class AlphaHelper\n{\n}\n");

        AssertRefused(alpha, FileLocalDirectives.DirectiveStateReason, "#define directive at line 1");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS1061 ", StringComparison.Ordinal));
    }

    [Fact]
    public void ChecksumPragmaOnAType_IsRefusedWithTheRestOfItsFamily()
    {
        // A checksum pragma changes debug information only, which a compile here cannot
        // show. It is refused because it too applies to the lines below it.
        var alpha = Write("Alpha.cs", Alpha + "#pragma checksum \"Alpha.cs\" \"{406EA660-64CF-4C82-B6F0-42D48172A799}\" \"ab007f1d23d9\"\n" + Sibling + "file class AlphaHelper\n{\n}\n");

        AssertRefused(alpha, FileLocalDirectives.DirectiveStateReason, "#pragma directive at line 7");
    }

    [Fact]
    public void StateDirectiveAboveTheNamespace_ReachesEveryOutput_SoTheInputSplits()
    {
        // A directive attached to the namespace keyword belongs to no type. Every output
        // repeats it, so nothing is lost and the refusal does not apply.
        var alpha = Write("Alpha.cs", Nullable + Alpha + "internal class Sibling\n{\n" + Name + "}\n\nfile class AlphaHelper\n{\n}\n");

        Run(Phase.Plan, alpha);
        var row = Assert.Single(Run(Phase.Content, alpha));

        Assert.Equal("Sibling", Assert.Single(row.NewFiles).Type);
        Assert.Contains("#nullable enable", File.ReadAllText(PathOf("Sibling.cs")), StringComparison.Ordinal);
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }
}
