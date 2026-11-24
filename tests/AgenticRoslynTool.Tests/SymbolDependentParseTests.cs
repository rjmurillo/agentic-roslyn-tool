using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The tool defines no preprocessor symbols. A file whose #if and #elif branches all name
// symbols can leave no branch live here, and the code around it then does not parse,
// although every real build compiles it. BenchmarkDotNet's HardwareIntrinsics.cs has this
// shape. Such a file is skipped, never failed. Decision 33.
public sealed class SymbolDependentParseTests
{
    [Fact]
    public void FileThatParsesOnlyWithItsSymbols_IsSkippedNotFailed()
    {
        var result = PlanOne(
            "namespace Sample;\n" +
            "\n" +
            "internal static class Intrinsics\n" +
            "{\n" +
            "    internal static bool IsSupported =>\n" +
            "#if NET6_0_OR_GREATER\n" +
            "        true;\n" +
            "#elif NETSTANDARD\n" +
            "        false;\n" +
            "#endif\n" +
            "\n" +
            "    internal static int Other => 1;\n" +
            "}\n" +
            "\n" +
            "internal class Second { }\n");

        Assert.Equal("skipped", result.Status);
        Assert.Equal(SourceParsing.SymbolDependentReason, result.Reason);
    }

    [Fact]
    public void BrokenFile_WithConditionals_StillFails()
    {
        // The retry defines every symbol the file names. A file that is broken either way
        // is not valid C#, so it keeps the failed status.
        var result = PlanOne(
            "namespace Sample;\n" +
            "\n" +
            "internal class Broken\n" +
            "{\n" +
            "#if DEBUG\n" +
            "    private int x;\n" +
            "#endif\n" +
            "    void M( }\n" +
            "}\n" +
            "\n" +
            "internal class Second { }\n");

        Assert.Equal("failed", result.Status);
        Assert.StartsWith("input has syntax errors:", result.Reason);
    }

    [Fact]
    public void BrokenFile_NamingTooManySymbols_IsSkippedWithoutTheRetry()
    {
        // The retry defines every symbol at once, and its parse time grows faster than the
        // symbol count. Past the cap the file is skipped unjudged, never failed.
        var groups = string.Concat(Enumerable.Range(0, SourceParsing.MaxRetrySymbols + 1).Select(i => $"#if S{i}\n#endif\n"));
        var result = PlanOne("namespace Sample;\n\ninternal class Broken\n{\n" + groups + "    void M( }\n}\n\ninternal class Second { }\n");

        Assert.Equal("skipped", result.Status);
        Assert.Equal(SourceParsing.TooManySymbolsReason, result.Reason);
    }

    [Fact]
    public void BrokenFile_NamingExactlyTheCap_StillGetsTheRetry()
    {
        var groups = string.Concat(Enumerable.Range(0, SourceParsing.MaxRetrySymbols).Select(i => $"#if S{i}\n#endif\n"));
        var result = PlanOne("namespace Sample;\n\ninternal class Broken\n{\n" + groups + "    void M( }\n}\n\ninternal class Second { }\n");

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public void RetryOverModerateNesting_StillJudges()
    {
        var result = PlanOne(DeepUnderIf(500));

        Assert.Equal("failed", result.Status);
    }

    [Theory]
    // Each shape overflowed the stack in an in-process retry and ended the run. Review found
    // them one at a time: generic arguments, interpolated strings, and parentheses.
    [InlineData("internal class Z { object f = default(" + "__GENERIC__" + "); }")]
    [InlineData("internal class Z { string f = " + "__INTERP__" + "; }")]
    [InlineData("internal class Z { int f = " + "__PARENS__" + "; }")]
    public void RetryThatCrashes_SkipsOnlyThatFile(string deepType)
    {
        var body = deepType
            .Replace("__GENERIC__", string.Concat(Enumerable.Repeat("A<", 20_000)) + "int" + new string('>', 20_000), StringComparison.Ordinal)
            .Replace("__INTERP__", string.Concat(Enumerable.Repeat("$\"{", 20_000)) + "0" + string.Concat(Enumerable.Repeat("}\"", 20_000)), StringComparison.Ordinal)
            .Replace("__PARENS__", new string('(', 1_000_000) + "0" + new string(')', 1_000_000), StringComparison.Ordinal);

        var result = PlanOne("namespace Sample;\n\n#if A\n" + body + "\n#endif\n\ninternal class Broken { void M( }\ninternal class Second { }\n");

        Assert.Equal("skipped", result.Status);
        Assert.Equal(SourceParsing.RetryUnjudgedReason, result.Reason);
    }

    [Fact]
    public void RetryPastItsTimeLimit_SkipsTheFileUnjudged()
    {
        // Parse time grows with the square of nesting depth. The child is killed at its limit.
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(DeepUnderIf(40_000), SourceParsing.Options);

        Assert.Equal(SourceParsing.RetryUnjudgedReason, SourceParsing.SymbolRetrySkipReason(tree, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void RetryChild_ReadsSymbolsThenText()
    {
        var source = "namespace Sample;\n#if A\ninternal class Z { }\n#else\ninternal class Z {\n#endif\n";

        Assert.Equal(0, SymbolRetry.RunChild(new StringReader("1\nA\n" + source)));
        Assert.Equal(1, SymbolRetry.RunChild(new StringReader("0\n" + source)));
    }

    private static string DeepUnderIf(int depth) =>
        "namespace Sample;\n\n#if A\ninternal class Z { int f = " + new string('(', depth) + "0" + new string(')', depth) + "; }\n#endif\n\n" +
        "internal class Broken { void M( }\ninternal class Second { }\n";

    [Fact]
    public void BrokenFile_WithNoConditionals_StillFails()
    {
        var result = PlanOne("namespace Sample;\n\ninternal class Broken { void M( }\ninternal class Second { }\n");

        Assert.Equal("failed", result.Status);
    }

    private static FileResult PlanOne(string source)
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("Intrinsics.cs", source);
        var options = new Options(ws.WriteInputList(path), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null);
        return Assert.Single(new FileSplitter(options).Run());
    }
}
