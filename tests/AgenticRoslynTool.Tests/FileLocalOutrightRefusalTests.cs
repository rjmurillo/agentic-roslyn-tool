using Xunit;

namespace AgenticRoslynTool.Tests;

// The inputs refused before any type is chosen to move. Each test also compiles what the
// split would have written, to show what the refusal prevents.
public sealed class FileLocalOutrightRefusalTests : FileLocalTestBase
{
    private const string Header = "namespace Sample;\n\ninternal class Alpha\n{\n}\n\n";

    [Fact]
    public void ExtensionMethod_BindsWithoutNamingItsClass_SoTheInputIsRefused()
    {
        // The foreach names neither the class nor the method. No name scan can see it.
        var alpha = Write("Alpha.cs", Header
            + "internal class Caller\n{\n    public int Run()\n    {\n        var total = 0;\n        foreach (var item in 3)\n        {\n            total += item;\n        }\n\n        return total;\n    }\n}\n\n"
            + "file static class Numbers\n{\n    public static System.Collections.Generic.IEnumerator<int> GetEnumerator(this int count)\n    {\n        for (var index = 0; index < count; index++)\n        {\n            yield return index;\n        }\n    }\n}\n");

        AssertRefused(alpha, FileLocalTypes.ExtensionMembersReason, "Numbers declares extension members");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS1579 ", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionBlock_IsRefusedTheSameWay()
    {
        var alpha = Write("Alpha.cs", Header
            + "internal class Caller\n{\n    public int Run() => 4.Quad;\n}\n\n"
            + "file static class Numbers\n{\n    extension(int value)\n    {\n        public int Quad => value * 4;\n    }\n}\n");

        AssertRefused(alpha, FileLocalTypes.ExtensionMembersReason, "Numbers declares extension members");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS1061 ", StringComparison.Ordinal));
    }

    [Fact]
    public void UsingAlias_NamesAFileLocalType_SoTheInputIsRefused()
    {
        // Caller never writes AlphaHelper. Every output repeats the using directive, so
        // the new file would carry an alias to a type it cannot see.
        var alpha = Write("Alpha.cs", "using Helper = Sample.AlphaHelper;\n\n" + Header
            + "internal class Caller\n{\n    public string Run() => typeof(Helper).Name;\n}\n\nfile class AlphaHelper\n{\n}\n");

        AssertRefused(alpha, FileLocalTypes.UsingDirectiveReason, "a using directive names AlphaHelper");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS0234 ", StringComparison.Ordinal));
    }

    [Fact]
    public void UsingStatic_NamesAFileLocalType_SoTheInputIsRefused()
    {
        var alpha = Write("Alpha.cs", "using static Sample.Totals;\n\n" + Header
            + "internal class Caller\n{\n    public int Run() => Twice(2);\n}\n\nfile static class Totals\n{\n    public static int Twice(int value) => value * 2;\n}\n");

        AssertRefused(alpha, FileLocalTypes.UsingDirectiveReason, "a using directive names Totals");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS0103 ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("using Helper = Sample.Totals;", "typeof(Helper).Name.Length", "CS0234 ")]
    [InlineData("using static Sample.Totals;", "Twice(2)", "CS0103 ")]
    public void UsingDirectiveInsideANamespaceBlock_IsScannedToo(string directive, string expression, string diagnostic)
    {
        // A using directive may sit inside the namespace block. Every output repeats that
        // block's opening lines, so it is copied like one at the top of the file.
        var alpha = Write("Alpha.cs", "namespace Sample\n{\n    " + directive + "\n\n    internal class Alpha\n    {\n    }\n\n"
            + "    internal class Caller\n    {\n        public int Run() => " + expression + ";\n    }\n\n"
            + "    file static class Totals\n    {\n        public static int Twice(int value) => value * 2;\n    }\n}\n");

        AssertRefused(alpha, FileLocalTypes.UsingDirectiveReason, "a using directive names Totals");

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void ConditionallyExcludedText_CannotBeScanned_SoTheInputIsRefused()
    {
        // The tool parses with no symbols defined, so it never sees the use inside #if.
        var alpha = Write("Alpha.cs", Header
            + "internal class Caller\n{\n#if EXTRA\n    public string Extra() => typeof(AlphaHelper).Name;\n#endif\n}\n\nfile class AlphaHelper\n{\n}\n");
        var text = File.ReadAllText(alpha);

        AssertRefused(alpha, FileLocalTypes.ExcludedTextReason, null);

        Assert.Empty(FileLocalCompiler.Diagnostics(FileLocalCompiler.Compile([(alpha, text)], "EXTRA")));
        var split = FileLocalCompiler.Compile(FileLocalCompiler.SplitUnchecked(alpha, text), "EXTRA");
        Assert.Contains(FileLocalCompiler.Diagnostics(split), d => d.StartsWith("CS0246 ", StringComparison.Ordinal));
    }

    [Fact]
    public void FileLocalTypeInANestedNamespace_WouldBeCopied_SoTheInputIsRefused()
    {
        // The tool places types one namespace down and no deeper. It would repeat the
        // nested block in every output, and each copy would be a new file-local type.
        var alpha = Write("Alpha.cs",
            "namespace Sample\n{\n    internal class Alpha\n    {\n    }\n\n    internal class Second\n    {\n    }\n\n"
            + "    namespace Inner\n    {\n        file class Hidden\n        {\n        }\n    }\n}\n");
        var text = File.ReadAllText(alpha);

        AssertRefused(alpha, FileLocalTypes.NestedNamespaceReason, null);

        Assert.Equal(1, FileLocalCompiler.CountDeclared(FileLocalCompiler.Compile([(alpha, text)]), "Hidden"));
        var split = FileLocalCompiler.Compile(FileLocalCompiler.SplitUnchecked(alpha, text));
        Assert.Empty(FileLocalCompiler.Diagnostics(split));
        Assert.Equal(2, FileLocalCompiler.CountDeclared(split, "Hidden"));
    }

    [Fact]
    public void OrdinaryTypeInANestedNamespace_WouldBeDeclaredTwice_SoTheInputIsRefused()
    {
        // The file-local type is one the tool can see. The nested block is not, and no
        // scan reads it, so Deep could also name the file-local type unseen.
        var alpha = Write("Alpha.cs",
            "namespace Sample\n{\n    internal class Alpha\n    {\n    }\n\n    internal class Second\n    {\n    }\n\n    file class AlphaHelper\n    {\n    }\n\n"
            + "    namespace Inner\n    {\n        internal class Deep\n        {\n        }\n    }\n}\n");

        AssertRefused(alpha, FileLocalTypes.NestedNamespaceReason, null);

        Assert.Contains(FileLocalCompiler.Diagnostics(CompileUncheckedSplit(alpha)), d => d.StartsWith("CS0101 ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("internal class Alpha\n{\n}\n\nfile class AlphaHelper\n{\n}\n")]
    [InlineData("file class First\n{\n}\n\nfile class Second\n{\n}\n")]
    public void AtMostOneOrdinaryType_LeavesNothingToMove(string types)
    {
        var alpha = Write("Alpha.cs", "namespace Sample;\n\n" + types);

        AssertRefused(alpha, FileLocalTypes.NothingToSplitReason, null);
    }
}
