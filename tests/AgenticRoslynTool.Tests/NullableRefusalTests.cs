using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static AgenticRoslynTool.Tests.NullableSplitFixture;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Pins the rule in <c>NullableGuard</c>: a <c>#nullable</c> that a split can move is refused
/// when the file holds any <c>#if</c>, or when it comes before a <c>using</c> directive. Each
/// refused row is a shape that review found the restated context gets wrong in some build or
/// for some line; the oracle rows show the unchecked split really does.
/// </summary>
public sealed class NullableRefusalTests
{
    [Theory]
    [InlineData("public class Alpha\n{\n#if NULLABLE_ON\n#nullable enable\n#endif\n}\n\npublic class Beta { }\n")]
    [InlineData("public class Alpha\n{\n#if NULLABLE_OFF\n#nullable disable\n#else\n#nullable enable\n#endif\n}\n\npublic class Beta { }\n")]
    [InlineData("public class Alpha\n{\n#if NULLABLE_ON\n#if NESTED\n#nullable enable\n#endif\n#endif\n}\n\npublic class Beta { }\n")]
    [InlineData("#if !LEGACY\n#nullable enable\n[System.Serializable]\n#endif\npublic class Alpha { }\n\npublic class Beta { }\n")]
    [InlineData("[System.Serializable]\n#if X\n#nullable enable\n#endif\npublic class Alpha { }\n\npublic class Beta { }\n")]
    [InlineData("public\n#if X\n#nullable enable\n#endif\nclass Alpha { }\n\npublic class Beta { }\n")]
    [InlineData("public class Alpha\n{\n#if false\n#nullable enable\n#endif\n}\n\npublic class Beta { }\n")]
    [InlineData("public class Alpha { }\n\npublic class Beta\n{\n#if NULLABLE_ON\n#nullable enable\n#endif\n}\n")]
    public void NullableAndAnIf_RefusesThePlan(string types)
    {
        Assert.Equal(FileSplitter.NullableGuardReason, PlanRow(Namespace + types).Reason);
    }

    [Theory]
    [InlineData(TravelsWithItsType)]
    [InlineData("#nullable enable\npublic class Alpha { }\n\n#if !X\n#nullable disable warnings\n[System.Serializable]\n#endif\npublic class Beta { public string? S; }\n")]
    [InlineData(LostAboveAType)]
    [InlineData(DefineUnderIf)]
    [InlineData("#if !X\n#undef EXTRA\n[System.Serializable]\n#endif\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n")]
    [InlineData("#define EXTRA\n[assembly: System.CLSCompliant(false)]\n#nullable enable\nnamespace N;\n\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n")]
    [InlineData(TypeUnderElse)]
    public void ShapeReviewFound_RefusesThePlan(string source)
    {
        Assert.Equal(FileSplitter.NullableGuardReason, PlanRow(source).Reason);
    }

    [Theory]
    [InlineData(UsingAfterNullable)]
    [InlineData(UsingAfterAssemblyAttribute)]
    public void NullableBeforeAUsing_RefusesThePlan(string source)
    {
        // The second row's #nullable sits above an [assembly:] attribute, which only the kept
        // file keeps, so the using alias in Beta's file would lose it.
        Assert.Equal(FileSplitter.NullableGuardReason, PlanRow(source).Reason);
    }

    [Theory]
    [InlineData(HiddenInAComment)]
    [InlineData(HiddenInARawString)]
    public void NullableThatOnlySomeBuildsSee_RefusesThePlan(string source)
    {
        // With no symbols the #nullable line is inside a comment or a string, so the parse
        // the tool reads holds no #nullable. With X defined it is a live directive above Beta.
        Assert.Equal(FileSplitter.NullableGuardReason, PlanRow(source).Reason);
    }

    [Theory]
    [InlineData("\n\u00A0#")]
    [InlineData("\n\f#")]
    [InlineData("\n\v#")]
    [InlineData("\n\uFEFF#")]
    [InlineData("\n#\u00A0")]
    [InlineData("\r#")]
    public void NullableTheCompilerReadsPastUnusualWhitespace_RefusesThePlan(string lineStart)
    {
        // Each row is a line the compiler reads as #nullable but a [ \t] and \n scan did not.
        var source = UnderIfInAlpha(lineStart);

        Assert.Equal(FileSplitter.NullableGuardReason, PlanRow(source).Reason);

        var outputs = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Select(output => output.Text).ToArray();
        Assert.Contains("symbols [", NullableContextOracle.FindChange(source, outputs));
    }

    [Theory]
    [InlineData("\u0085#")]
    [InlineData("\u2028#")]
    [InlineData("\u2029#")]
    public void NullableAfterAUnicodeLineBreak_TheGuardRefuses(string lineStart)
    {
        // The plan refuses these sooner, as an empty directive shell, so ask the guard itself.
        var root = CSharpSyntaxTree.ParseText(UnderIfInAlpha(lineStart)).GetCompilationUnitRoot();

        Assert.True(NullableGuard.Refuses(root, TopLevelType.Find(root).ToArray()));
    }

    [Theory]
    [InlineData(TravelsWithItsType)]
    [InlineData(LostAboveAType)]
    [InlineData(DefineUnderIf)]
    [InlineData(TypeUnderElse)]
    [InlineData(HiddenInAComment)]
    [InlineData(HiddenInARawString)]
    public void Oracle_SeesWhatTheUncheckedSplitGetsWrongInABuildWithASymbol(string source)
    {
        var outputs = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Select(output => output.Text).ToArray();

        Assert.Contains("symbols [", NullableContextOracle.FindChange(source, outputs));
    }

    [Fact]
    public void UncheckedSplit_LeavesAUsingAliasOutsideTheContextItHad()
    {
        // The oracle reads types, not using directives, so this row asks the compiler directly.
        var beta = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", UsingAfterNullable).Single(output => output.Path.EndsWith("Beta.cs", StringComparison.Ordinal));

        Assert.DoesNotContain("CS8632", NullableContextOracle.DiagnosticIds(UsingAfterNullable));
        Assert.Contains("CS8632", NullableContextOracle.DiagnosticIds(beta.Text));
    }

    [Fact]
    public void IfWithANullableOnlyAboveTheNamespace_SplitsUnchanged()
    {
        using var ws = new TempWorkspace();
        var beta = "public class Beta\n{\n#if X\n    public string? S;\n#endif\n}\n";
        var source = "#nullable enable\n" + Namespace + Alpha + beta;

        SplitPreserved(ws, source);

        Assert.Equal("#nullable enable\n" + Namespace + beta, Read(ws, "Beta.cs"));
    }

    [Fact]
    public void UsingBeforeAMovableNullable_StillSplits()
    {
        using var ws = new TempWorkspace();
        var source = "using System;\n\n" + Namespace + Alpha + "#nullable enable\npublic class Beta { public string? S; }\n\npublic class Gamma { public string? G; }\n";

        SplitPreserved(ws, source);

        Assert.Equal(1, CountLine(Read(ws, "Gamma.cs"), "#nullable enable"));
    }

    private const string TravelsWithItsType = "#nullable enable\npublic class Alpha { }\n\n#if !X\n#nullable disable\n[System.Serializable]\n#endif\npublic class Beta { public string? S; }\n";

    private const string LostAboveAType = "#nullable disable\n#if !X\npublic class Alpha { }\n#nullable enable\n#endif\npublic class Beta { public string? S; }\n";

    private const string DefineUnderIf = "#if !X\n#define EXTRA\n[System.Serializable]\n#endif\n#nullable enable\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n";

    private const string TypeUnderElse = "namespace N;\n#nullable enable\n#if !NETFRAMEWORK\npublic class Alpha { public string? Name { get; init; } }\n#else\npublic class Alpha { public string? Name { get; set; } }\n#endif\n#nullable disable\npublic class Beta { }\n";

    private const string UsingAfterAssemblyAttribute = "#nullable enable\n[assembly: System.CLSCompliant(false)]\n\nnamespace N\n{\n    using L = System.Collections.Generic.List<string?>;\n\n    public class Alpha { public L? A; }\n\n    public class Beta { public L? B; }\n}\n";

    private const string HiddenInAComment = "namespace N;\n\npublic class Alpha\n{\n#if !X\n/*\n#endif\n#nullable enable\n#if !X\n*/\n#endif\n    public string? F;\n}\n\npublic class Beta\n{\n    public string? G;\n}\n";

    private const string HiddenInARawString = "public class Alpha\n{\n#if !X\n    public const string S = \"\"\"\n#endif\n#nullable enable\n#if !X\n\"\"\";\n#endif\n}\n\npublic class Beta { public string? G; }\n";

    private const string UsingAfterNullable = "namespace N1\n{\n#nullable enable\n    public class Alpha { public string? A; }\n}\n\nnamespace N2\n{\n    using L = System.Collections.Generic.List<string?>;\n\n    public class Beta { public L? B; }\n}\n";

    private static string UnderIfInAlpha(string lineStart)
    {
        return "namespace N;\n\npublic class Alpha\n{\n#if X" + lineStart + "nullable enable\n#endif\n}\n\npublic class Beta\n{\n    public string? B;\n}\n";
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
