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
    public void RetryOverDeepCodeTheFirstParseSkipped_FinishesAndJudges()
    {
        // The retry parses code under #if A that the first parse skipped. It runs on its own
        // thread with a large stack, and a moderate nest there still gets judged.
        var result = PlanOne(DeepUnderIf(5_000));

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public void RetryPastItsTimeLimit_SkipsTheFileUnjudged()
    {
        // Parse time grows with the square of nesting depth. A retry that runs past its limit
        // is cancelled, and the file is skipped, never failed and never split.
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(DeepUnderIf(20_000), SourceParsing.Options);

        Assert.Equal(SourceParsing.RetryTooSlowReason, SourceParsing.SymbolRetrySkipReason(tree, TimeSpan.FromMilliseconds(50)));
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
