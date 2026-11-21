using Xunit;
using static AgenticRoslynTool.Tests.NullableSplitFixture;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// A <c>#nullable</c> directive under <c>#if</c> sets a context only in some builds. The tool
/// parses with no symbols, so it refuses a split that would drop one rather than restate it.
/// An <c>#if</c> group outside a type body was already refused as crossing type boundaries,
/// so a group inside a body is the shape this guard meets.
/// </summary>
public sealed class NullableConditionalTests
{
    [Theory]
    [InlineData("#if NULLABLE_ON\n#nullable enable\n#endif\n")]
    [InlineData("#if NULLABLE_OFF\n#nullable disable\n#else\n#nullable enable\n#endif\n")]
    [InlineData("#if NULLABLE_ON\n#if NESTED\n#nullable enable\n#endif\n#endif\n")]
    public void ConditionalDirectiveInARemovedBody_RefusesThePlan(string conditional)
    {
        using var ws = new TempWorkspace();
        var source = Namespace + "public class Alpha\n{\n" + conditional + "}\n\npublic class Beta { }\n";
        var alphaPath = ws.WriteFile("Alpha.cs", source);

        var row = Assert.Single(new FileSplitter(new Options(ws.WriteInputList(alphaPath), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null)).Run());

        Assert.Equal("skipped", row.Status);
        Assert.Equal(FileSplitter.ConditionalNullableReason, row.Reason);
    }

    [Fact]
    public void ConditionalDirectiveInTheMovedTypesOwnBody_TravelsWithIt()
    {
        using var ws = new TempWorkspace();
        var beta = "public class Beta\n{\n#if NULLABLE_ON\n#nullable enable\n#endif\n}\n";
        var source = Namespace + Alpha + beta;

        SplitPreserved(ws, source);

        Assert.Equal(Namespace + beta, Read(ws, "Beta.cs"));
    }
}
