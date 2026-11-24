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
        var result = PlanOne(DeepUnderIf(SourceParsing.MaxRetryDepth - 10));

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public void RetryOverDeepCodeTheFirstParseSkipped_IsSkippedBeforeItParses()
    {
        // The retry parses code under #if A that the first parse skipped. 500,000 nested
        // parentheses there overflowed even a 256 MB stack and ended the run. The depth check
        // runs first, on the lexer, so this returns at once.
        var result = PlanOne(DeepUnderIf(500_000));

        Assert.Equal("skipped", result.Status);
        Assert.Equal(SourceParsing.TooDeepReason, result.Reason);
    }

    [Fact]
    public void DepthCheck_IsNotFooledByBracketsInStrings()
    {
        // A raw text count would read ( ")" ( ")" ... as depth 1. The lexer reads the strings
        // as tokens, so the real depth counts.
        var nest = string.Concat(Enumerable.Repeat("(\")\" + ", SourceParsing.MaxRetryDepth + 10)) + "0" + new string(')', SourceParsing.MaxRetryDepth + 10);
        var result = PlanOne("namespace Sample;\n\n#if A\ninternal class Z { object f = " + nest + "; }\n#endif\n\ninternal class Broken { void M( }\ninternal class Second { }\n");

        Assert.Equal(SourceParsing.TooDeepReason, result.Reason);
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
