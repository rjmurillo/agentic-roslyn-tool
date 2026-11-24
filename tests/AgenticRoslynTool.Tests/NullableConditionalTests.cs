using Xunit;
using static AgenticRoslynTool.Tests.NullableSplitFixture;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// A <c>#nullable</c> directive under <c>#if</c> sets a context only in some builds. The tool
/// parses with no symbols, so it cannot restate one. It refuses a file where such a directive
/// starts before the last type, because then some type's context depends on the build. The
/// rows sample places such a directive can sit. The older directive guards refuse some of
/// these files first, with their own reasons, and none of these rows reaches them.
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
        Assert.Equal(FileSplitter.ConditionalNullableReason, PlanRow(Namespace + alpha + "\npublic class Beta { }\n").Reason);
    }

    [Theory]
    [InlineData("#nullable enable\npublic class Alpha { }\n\n#if !X\n#nullable disable\n[System.Serializable]\n#endif\npublic class Beta { public string? S; }\n")]
    [InlineData("#nullable enable\npublic class Alpha { }\n\n#if !X\n#nullable disable warnings\n[System.Serializable]\n#endif\npublic class Beta { public string? S; }\n")]
    [InlineData("#nullable disable\n#if !X\npublic class Alpha { }\n#nullable enable\n#endif\npublic class Beta { public string? S; }\n")]
    public void ConditionalDirectiveThatTravelsWithItsType_StillRefusesThePlan(string source)
    {
        // The conditional directive stays in Beta's file, so no output loses it. An unconditional
        // directive above it is lost instead, and with X defined only that directive set Beta's
        // context. Restoring from a parse with no symbols cannot see that, so the file is refused.
        Assert.Equal(FileSplitter.ConditionalNullableReason, PlanRow(source).Reason);
    }

    [Theory]
    [InlineData("#if !X\n#define EXTRA\n[System.Serializable]\n#endif\n#nullable enable\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n")]
    [InlineData("#if !X\n#undef EXTRA\n[System.Serializable]\n#endif\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n")]
    public void ConditionalDefineBesideAConditionalNullable_RefusesThePlan(string source)
    {
        // A split drops the #if around a #define or #undef that sits above the first type, so
        // the symbol changes in every new file. A #nullable under #if that reads the symbol then
        // sets a different context in some build, even inside the last type.
        Assert.Equal(FileSplitter.ConditionalNullableReason, PlanRow(source).Reason);
    }

    [Theory]
    [InlineData("#nullable enable\npublic class Alpha { }\n\n#if !X\n#nullable disable\n[System.Serializable]\n#endif\npublic class Beta { public string? S; }\n")]
    [InlineData("#nullable disable\n#if !X\npublic class Alpha { }\n#nullable enable\n#endif\npublic class Beta { public string? S; }\n")]
    [InlineData("#if !X\n#define EXTRA\n[System.Serializable]\n#endif\n#nullable enable\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n")]
    public void Oracle_SeesAContextThatOnlyADefinedSymbolChanges(string source)
    {
        // Without the guard the split writes these files, and they hold the same context as the
        // source in a build that defines no symbol. The oracle must still report the change.
        var outputs = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Select(output => output.Text).ToArray();

        Assert.Contains("symbols [X]", NullableContextOracle.FindChange(source, outputs));
    }

    [Fact]
    public void ConditionalDirectiveInTheLastTypesOwnBody_TravelsWithIt()
    {
        using var ws = new TempWorkspace();
        var beta = "public class Beta\n{\n#if NULLABLE_ON\n#nullable enable\n#endif\n}\n";
        var source = Namespace + Alpha + beta;

        SplitPreserved(ws, source);

        Assert.Equal(Namespace + beta, Read(ws, "Beta.cs"));
    }

    private static FileResult PlanRow(string source)
    {
        using var ws = new TempWorkspace();
        var alphaPath = ws.WriteFile("Alpha.cs", source);
        var row = Assert.Single(new FileSplitter(new Options(ws.WriteInputList(alphaPath), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null)).Run());
        Assert.Equal("skipped", row.Status);
        return row;
    }
}
