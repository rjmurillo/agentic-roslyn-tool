using Xunit;

namespace AgenticRoslynTool.Tests;

// The compiler rules the name scan leans on. Decision 30 argues that a moved type reaches a
// file-local type by writing its name, by an extension member, or by inference from a base
// list, and that all three are refused. The compiler forbids the other routes found. If a later compiler
// relaxes one of these, the argument needs another look, and this fails first.
public sealed class FileLocalPremiseTests : FileLocalTestBase
{
    [Theory]
    [InlineData("CS9051", "internal class Alpha\n{\n    internal Helper Get() => null;\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9051", "internal class Alpha<T>\n    where T : Helper\n{\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9051", "internal class Alpha\n{\n    internal event System.Action<Helper> Changed;\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9051", "internal class Alpha(Helper helper)\n{\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9051", "internal class Alpha\n{\n    internal delegate Helper Factory();\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9053", "internal class Base<T>\n{\n}\n\ninternal class Alpha : Base<Helper>\n{\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9053", "internal interface IMarker<T>\n{\n}\n\ninternal interface IAlpha : IMarker<Helper>\n{\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9053", "internal class Alpha : Helper\n{\n}\n\nfile class Helper\n{\n}\n")]
    [InlineData("CS9071", "internal class Helper\n{\n}\n\nfile class Helper\n{\n}\n")]
    public void Compiler_ForbidsTheRoutesANameScanCannotSee(string diagnostic, string types)
    {
        // CS9051: no file-local type in the signature of an ordinary type's member, nor
        // in a constraint, an event, a primary constructor, or a delegate another type can
        // reach. CS9053: no file-local type in the base class of an ordinary
        // type, nor in the interfaces of an ordinary interface.
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

    [Fact]
    public void InferenceThroughAnInterfaceList_WouldRebindACall_SoTheInputIsRefused()
    {
        // The compiler lets a class implement an interface whose type argument is a
        // file-local type. Mover never writes AlphaHelper. It gets one by inference from
        // Alpha's interface list. In another file the call on it binds to the extension
        // on object, with no diagnostic: the program printed 1 before and 2 after.
        const string Source =
            "namespace Sample;\n\ninternal interface IMarker<T>\n    where T : new()\n{\n    T Make() => new T();\n}\n\n"
            + "internal static class Maker\n{\n    public static T Make<T>(IMarker<T> marker)\n        where T : new() => marker.Make();\n}\n\n"
            + "internal static class Fallback\n{\n    public static int Value(this object anything) => 2;\n}\n\n"
            + "internal class Alpha : IMarker<AlphaHelper>\n{\n}\n\n"
            + "internal class Mover\n{\n    public static int Run()\n    {\n        var item = Maker.Make(new Alpha());\n        return item.Value();\n    }\n}\n\n"
            + "file class AlphaHelper\n{\n    public int Value() => 1;\n}\n";
        var alpha = Write("Alpha.cs", Source);

        AssertRefused(alpha, FileLocalTypes.BaseListReason, "Alpha lists AlphaHelper among its base types");

        Assert.Equal("Sample.AlphaHelper", FileLocalCompiler.BoundMemberOwner(Compile(), "Mover", "Value"));
        var split = CompileUncheckedSplit(alpha);
        Assert.Empty(FileLocalCompiler.Diagnostics(split));
        Assert.Equal("Sample.Fallback", FileLocalCompiler.BoundMemberOwner(split, "Mover", "Value"));
    }

    [Theory]
    [InlineData("internal class Alpha\n{\n    internal class Inner : IMarker<AlphaHelper>\n    {\n    }\n}\n", "new Alpha.Inner()", "Sample.AlphaHelper")]
    [InlineData("internal struct Alpha : IMarker<AlphaHelper[]>\n{\n}\n", "new Alpha()", "Sample.AlphaHelper[]")]
    [InlineData("internal record Alpha : IMarker<(AlphaHelper First, int Second)>;\n", "new Alpha()", "(Sample.AlphaHelper First, int Second)")]
    [InlineData("internal class Alpha : System.IDisposable, IMarker<AlphaHelper>\n{\n    public void Dispose()\n    {\n    }\n}\n", "new Alpha()", "Sample.AlphaHelper")]
    public void EveryBaseListThatNamesAFileLocalType_IsRefused(string alphaType, string argument, string inferred)
    {
        // The compiler accepts each of these in one file: a nested type, an array, a
        // tuple, and a second interface. Each gives another file the same way in. Without
        // the refusal, Mover holds a value of the file-local type and never writes its name.
        var alpha = Write("Alpha.cs", "namespace Sample;\n\ninternal interface IMarker<T>\n{\n}\n\n"
            + "internal static class Maker\n{\n    public static T Make<T>(IMarker<T> marker) => default;\n}\n\n" + alphaType
            + "\ninternal class Mover\n{\n    public static object Run()\n    {\n        var item = Maker.Make(" + argument + ");\n        return item;\n    }\n}\n\nfile class AlphaHelper\n{\n}\n");

        AssertRefused(alpha, FileLocalTypes.BaseListReason, "Alpha lists AlphaHelper among its base types");

        var split = CompileUncheckedSplit(alpha);
        Assert.Equal(inferred, FileLocalCompiler.LocalType(split, "Mover", "item"));
        Assert.DoesNotContain(split.SyntaxTrees, tree => Path.GetFileName(tree.FilePath) == "Mover.cs" && tree.ToString().Contains("AlphaHelper", StringComparison.Ordinal));
    }
}
