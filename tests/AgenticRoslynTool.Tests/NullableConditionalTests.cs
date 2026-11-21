using Xunit;
using static AgenticRoslynTool.Tests.NullableSplitFixture;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// A <c>#nullable</c> directive under <c>#if</c> sets a context only in some builds. The tool
/// parses with no symbols, so it refuses a split that would drop one rather than restate it.
/// Two older guards run first: a group no single type owns crosses type boundaries, and a
/// split that leaves an empty group is refused. Every group they let
/// through reaches this guard. The rows sample places such a group can sit.
/// </summary>
public sealed class NullableConditionalTests
{
    [Theory]
    [InlineData("public class Alpha\n{\n#if NULLABLE_ON\n#nullable enable\n#endif\n}\n")]
    [InlineData("public class Alpha\n{\n#if NULLABLE_OFF\n#nullable disable\n#else\n#nullable enable\n#endif\n}\n")]
    [InlineData("public class Alpha\n{\n#if NULLABLE_ON\n#if NESTED\n#nullable enable\n#endif\n#endif\n}\n")]
    [InlineData("#if !LEGACY\n#nullable enable\n[System.Serializable]\n#endif\npublic class Alpha { }\n")]
    [InlineData("[System.Serializable]\n#if X\n#nullable enable\n#endif\npublic class Alpha { }\n")]
    [InlineData("public\n#if X\n#nullable enable\n#endif\nclass Alpha { }\n")]
    [InlineData("public class Alpha\n{\n#if false\n#nullable enable\n#endif\n}\n")]
    public void ConditionalDirectiveBeforeAMovedType_RefusesThePlan(string alpha)
    {
        using var ws = new TempWorkspace();
        var source = Namespace + alpha + "\npublic class Beta { }\n";
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
