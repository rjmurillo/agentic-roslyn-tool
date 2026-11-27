using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class SymbolDependentParseTests
{
    [Theory]
    [InlineData("#if FEATURE_A && !FEATURE_B")]
    [InlineData("#if false\n#elif FEATURE_A")]
    public void BuildSymbolsCouldChangeTheParse_SkipsWithoutSplitting(string conditional)
    {
        var result = PlanOne("internal class Host { int Value =>\n" + conditional +
            "\n1;\n#endif\n}\ninternal class Extra { }\n");

        Assert.Equal("skipped", result.Status);
        Assert.Equal(SourceParsing.SymbolDependentReason, result.Reason);
        Assert.Empty(result.NewFiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#if true\n#endif\n")]
    [InlineData("#if\n#endif\n")]
    [InlineData("#if 1\n#endif\n")]
    public void BrokenFile_WithoutNamedConditions_StillFails(string prefix)
    {
        var result = PlanOne(prefix + "internal class Broken { void M( }\n");

        Assert.Equal("failed", result.Status);
        Assert.StartsWith(FileSplitter.SyntaxErrorsPrefix, result.Reason);
    }

    private static FileResult PlanOne(string source)
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("Intrinsics.cs", source);
        var options = new Options(ws.WriteInputList(path), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null);
        return Assert.Single(new FileSplitter(options).Run());
    }
}
