using Xunit;

namespace AgenticRoslynTool.Tests;

// The compiler rules the name scan leans on. Decision 30 argues that a moved type can only
// reach a file-local type by writing its name. That holds because the compiler forbids the
// other routes, and because a binding made through a symbol does not look a name up. If a
// later compiler relaxes one of these, the proof needs another look, and this fails first.
public sealed class FileLocalPremiseTests : FileLocalTestBase
{
    [Theory]
    [InlineData("CS9051", "internal class Alpha\n{\n    internal Helper Get() => null;\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9053", "internal class Alpha : Helper\n{\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9071", "internal class Helper\n{\n}\n\nfile class Helper\n{\n}\n")]
    public void Compiler_ForbidsTheRoutesANameScanCannotSee(string diagnostic, string types)
    {
        // CS9051: no file-local type in the signature of an ordinary type's member.
        // CS9053: no file-local type as the base of an ordinary type.
        // CS9071: no two types of one name in one namespace of one file.
        var compilation = FileLocalCompiler.Compile([(PathOf("Alpha.cs"), "namespace Sample;\n\n" + types)]);

        Assert.Contains(FileLocalCompiler.Diagnostics(compilation), d => d.StartsWith(diagnostic + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void BindingThroughASymbol_StillWorksFromTheMovedFile()
    {
        // Mover never writes BagBuilder. It reaches the file-local builder through the
        // attribute on Bag, which stays beside the builder. That is a symbol, not a name
        // lookup, so the moved file still compiles.
        const string Source =
            "using System.Collections;\nusing System.Collections.Generic;\nusing System.Runtime.CompilerServices;\n\nnamespace Sample;\n\n"
            + "[CollectionBuilder(typeof(BagBuilder), \"Create\")]\ninternal class Bag : IEnumerable<int>\n{\n    public int Count { get; set; }\n\n"
            + "    public IEnumerator<int> GetEnumerator()\n    {\n        yield break;\n    }\n\n    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();\n}\n\n"
            + "file static class BagBuilder\n{\n    public static Bag Create(System.ReadOnlySpan<int> items) => new Bag { Count = items.Length };\n}\n\n"
            + "internal class Mover\n{\n    public static int Run()\n    {\n        Bag bag = [1, 2, 3];\n        return bag.Count;\n    }\n}\n";
        var bag = Write("Bag.cs", Source);
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));

        Run(Phase.Plan, bag);
        var row = Assert.Single(Run(Phase.Content, bag));

        Assert.Equal("Mover", Assert.Single(row.NewFiles).Type);
        Assert.DoesNotContain("BagBuilder", File.ReadAllText(PathOf("Mover.cs")), StringComparison.Ordinal);
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }
}
