using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static AgenticRoslynTool.Tests.NullableSplitFixture;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Pins the rule in <c>NullableGuard</c>: a <c>#nullable</c> that a split can move is refused
/// beside an <c>#if</c>, before a <c>using</c> directive, or in a generated file. Compiler
/// checks show what an unchecked split changes. Other rows pin intentional over-refusal.
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
    public void NullableAndAnIf_RefusesThePlan(string types)
    {
        var source = Namespace + types;
        Assert.Equal(NullableGuard.Reason, PlanRow(source).Reason);

        var outputs = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Select(output => output.Text).ToArray();
        Assert.Contains("symbols [", NullableContextOracle.FindChange(source, outputs));
    }

    [Theory]
    [InlineData("public class Alpha\n{\n#if false\n#nullable enable\n#endif\n}\n\npublic class Beta { }\n")]
    [InlineData("public class Alpha { }\n\npublic class Beta\n{\n#if NULLABLE_ON\n#nullable enable\n#endif\n}\n")]
    public void NullableAndAnIfThatLoseNothing_RefuseAnyway(string types)
    {
        // Intended over-refusal. The guard does not read conditions, and it does not ask
        // which type a directive travels with. Neither row loses context unchecked.
        var source = Namespace + types;
        Assert.Equal(NullableGuard.Reason, PlanRow(source).Reason);

        var outputs = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Select(output => output.Text).ToArray();
        Assert.Null(NullableContextOracle.FindChange(source, outputs));
    }

    [Theory]
    [InlineData(TravelsWithItsType)]
    [InlineData(RestoresWarningsUnderIf)]
    [InlineData(LostAboveAType)]
    [InlineData(DefineUnderIf)]
    [InlineData(UndefUnderIf)]
    [InlineData(DefineAboveAssemblyAttribute)]
    [InlineData(TypeUnderElse)]
    public void ShapeReviewFound_RefusesThePlan(string source)
    {
        Assert.Equal(NullableGuard.Reason, PlanRow(source).Reason);
    }

    [Theory]
    [InlineData(UsingAfterNullable)]
    [InlineData(UsingAfterAssemblyAttribute)]
    public void NullableBeforeAUsing_RefusesThePlan(string source)
    {
        // The second row's #nullable sits above an [assembly:] attribute, which only the kept
        // file keeps, so the using alias in Beta's file would lose it.
        Assert.Equal(NullableGuard.Reason, PlanRow(source).Reason);
    }

    [Theory]
    [InlineData(HiddenInAComment)]
    [InlineData(HiddenInARawString)]
    public void NullableThatOnlySomeBuildsSee_RefusesThePlan(string source)
    {
        // With no symbols the #nullable line is inside a comment or a string, so the parse
        // the tool reads holds no #nullable. With X defined it is a live directive above Beta.
        Assert.Equal(NullableGuard.Reason, PlanRow(source).Reason);
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

        Assert.Equal(NullableGuard.Reason, PlanRow(source).Reason);

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

        Assert.True(NullableGuard.Refuses(root, TopLevelType.Find(root).ToArray(), root.SyntaxTree.FilePath));
    }

    [Theory]
    [InlineData(TravelsWithItsType)]
    [InlineData(LostAboveAType)]
    [InlineData(DefineUnderIf)]
    [InlineData(TypeUnderElse)]
    [InlineData(RestoresWarningsUnderIf)]
    [InlineData(UndefUnderIf)]
    [InlineData(DefineAboveAssemblyAttribute)]
    [InlineData(HiddenInAComment)]
    [InlineData(HiddenInARawString)]
    public void Oracle_SeesWhatTheUncheckedSplitGetsWrongInABuildWithASymbol(string source)
    {
        var outputs = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Select(output => output.Text).ToArray();

        Assert.Contains("symbols [", NullableContextOracle.FindChange(source, outputs));
    }

    [Theory]
    [InlineData(GeneratedRestoreLost)]
    [InlineData(GeneratedRestoreAdded)]
    [InlineData("/* <autogenerated /> */\n" + GeneratedBody)]
    public void NullableInAGeneratedFile_RefusesThePlan(string source)
    {
        // In generated code no directive means disabled, but #nullable restore means the
        // project default. The fold reads both as the default. The oracle reads contexts, and
        // both read as inherited, so this row asks the compiler for diagnostics instead.
        Assert.Equal(NullableGuard.Reason, PlanRow(source).Reason);

        var gamma = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Single(output => output.Path.EndsWith("Gamma.cs", StringComparison.Ordinal));
        const Microsoft.CodeAnalysis.NullableContextOptions enable = Microsoft.CodeAnalysis.NullableContextOptions.Enable;
        Assert.NotEqual(NullableContextOracle.DiagnosticIds(source, enable), NullableContextOracle.DiagnosticIds(gamma.Text, enable));
    }

    [Fact]
    public void NullableInAFileWithAGeneratedName_RefusesThePlan()
    {
        var source = GeneratedRestoreAdded.Replace("// <auto-generated/>\n", string.Empty, StringComparison.Ordinal);
        Assert.Equal(NullableGuard.Reason, PlanRow(source, "Alpha.g.cs").Reason);

        var gamma = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Single(output => output.Path.EndsWith("Gamma.cs", StringComparison.Ordinal));
        const Microsoft.CodeAnalysis.NullableContextOptions enable = Microsoft.CodeAnalysis.NullableContextOptions.Enable;
        Assert.Contains("#nullable restore\nclass Gamma", gamma.Text, StringComparison.Ordinal);
        var before = NullableContextOracle.DiagnosticIds(source, enable, "Alpha.g.cs");
        var after = NullableContextOracle.DiagnosticIds(gamma.Text, enable, gamma.Path);
        Assert.True(before.Contains("CS8669") && !before.Contains("CS8600"), string.Join(",", before));
        Assert.True(after.Contains("CS8600") && !after.Contains("CS8669"), string.Join(",", after));
    }

    [Fact]
    public void NullableBesideALineMapping_RefusesThePlan()
    {
        // A restated line inside a #line mapping shifts every mapped line below it, so a
        // CallerLineNumber value in Beta changes with no diagnostic.
        Assert.Equal(NullableGuard.Reason, PlanRow(LineMappedBeta).Reason);

        var beta = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", LineMappedBeta).Single(output => output.Path.EndsWith("Beta.cs", StringComparison.Ordinal));
        Assert.Equal(MappedLineOfCall(LineMappedBeta) + 1, MappedLineOfCall(beta.Text));
    }

    [Fact]
    public void KeptFileRenamedToAGeneratedName_RefusesThePlan()
    {
        // Foo.cs keeps TemporaryGeneratedFile_K and is renamed to its name. The content phase
        // reads the renamed file as generated and refuses it, so the plan must refuse first,
        // or renames would move a file that content then leaves unsplit.
        var row = PlanRow("namespace Demo;\n\npublic class TemporaryGeneratedFile_K { }\n\n#nullable enable\npublic class Beta { public string? B; }\n", "Foo.cs");

        Assert.Equal(NullableGuard.Reason, row.Reason);
    }

    [Theory]
    [InlineData("Alpha.g.cs")]
    [InlineData("Alpha.Designer.cs")]
    [InlineData("TemporaryGeneratedFile_Alpha.cs")]
    [InlineData("x\\TemporaryGeneratedFile_Alpha.cs")]
    [InlineData("x:TemporaryGeneratedFile_Alpha.cs")]
    public void NameTheCompilerReadsAsGenerated_TheGuardRefuses(string name)
    {
        // Each row first asks the compiler, so a name rule that drifts from it fails here. A
        // parsed path needs no file, so a name Windows cannot create still runs everywhere.
        var path = "/work/" + name;
        Assert.Contains("CS8669", NullableContextOracle.DiagnosticIds("class C { string? s; }", Microsoft.CodeAnalysis.NullableContextOptions.Enable, path));

        var root = CSharpSyntaxTree.ParseText(GeneratedBody, path: path).GetCompilationUnitRoot();
        Assert.True(NullableGuard.Refuses(root, TopLevelType.Find(root).ToArray(), root.SyntaxTree.FilePath));
    }

    [Theory]
    [InlineData(UsingAfterNullable)]
    [InlineData(UsingAfterAssemblyAttribute)]
    public void UncheckedSplit_LeavesAUsingAliasOutsideTheContextItHad(string source)
    {
        // The oracle reads types, not using directives, so these rows ask the compiler directly.
        var beta = FileLocalCompiler.SplitUnchecked("/work/Alpha.cs", source).Single(output => output.Path.EndsWith("Beta.cs", StringComparison.Ordinal));

        Assert.DoesNotContain("CS8632", NullableContextOracle.DiagnosticIds(source));
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

    [Theory]
    [InlineData("")]
    [InlineData("// <auto-generated/>\n")]
    public void NullableAboveTheUsings_IsNotMovable_SoTheFileSplits(string opening)
    {
        // Every output keeps a directive above the first removable span, so neither the
        // using rule nor the generated rule applies to it.
        using var ws = new TempWorkspace();
        var source = opening + "#nullable enable\nusing System;\n" + Namespace + "public class Alpha { public string? A; }\n\npublic class Beta { public string? B; }\n";

        SplitPreserved(ws, source);
    }

    [Fact]
    public void UsingBeforeAMovableNullable_StillSplits()
    {
        using var ws = new TempWorkspace();
        var source = "using System;\n\n" + Namespace + Alpha + "#nullable enable\npublic class Beta { public string? S; }\n\npublic class Gamma { public string? G; }\n";

        SplitPreserved(ws, source);

        Assert.Equal(1, CountLine(Read(ws, "Gamma.cs"), "#nullable enable"));
    }

    private const string RestoresWarningsUnderIf = "#nullable enable\npublic class Alpha { }\n\n#if !X\n#nullable disable warnings\n[System.Serializable]\n#endif\npublic class Beta { public string? S; }\n";

    private const string UndefUnderIf = "#if !X\n#undef EXTRA\n[System.Serializable]\n#endif\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n";

    private const string DefineAboveAssemblyAttribute = "#define EXTRA\n[assembly: System.CLSCompliant(false)]\n#nullable enable\nnamespace N;\n\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n";

    private const string TravelsWithItsType = "#nullable enable\npublic class Alpha { }\n\n#if !X\n#nullable disable\n[System.Serializable]\n#endif\npublic class Beta { public string? S; }\n";

    private const string LostAboveAType = "#nullable disable\n#if !X\npublic class Alpha { }\n#nullable enable\n#endif\npublic class Beta { public string? S; }\n";

    private const string DefineUnderIf = "#if !X\n#define EXTRA\n[System.Serializable]\n#endif\n#nullable enable\npublic class Alpha { }\n\npublic class Beta\n{\n#if EXTRA\n#nullable disable\n#endif\n    public string? S;\n}\n";

    private const string TypeUnderElse = "namespace N;\n#nullable enable\n#if !NETFRAMEWORK\npublic class Alpha { public string? Name { get; init; } }\n#else\npublic class Alpha { public string? Name { get; set; } }\n#endif\n#nullable disable\npublic class Beta { }\n";

    private const string UsingAfterAssemblyAttribute = "#nullable enable\n[assembly: System.CLSCompliant(false)]\n\nnamespace N\n{\n    using L = System.Collections.Generic.List<string?>;\n\n    public class Alpha { public L? A; }\n\n    public class Beta { public L? B; }\n}\n";

    private const string HiddenInAComment = "namespace N;\n\npublic class Alpha\n{\n#if !X\n/*\n#endif\n#nullable enable\n#if !X\n*/\n#endif\n    public string? F;\n}\n\npublic class Beta\n{\n    public string? G;\n}\n";

    private const string HiddenInARawString = "public class Alpha\n{\n#if !X\n    public const string S = \"\"\"\n#endif\n#nullable enable\n#if !X\n\"\"\";\n#endif\n}\n\npublic class Beta { public string? G; }\n";

    private const string GeneratedBody = "namespace Demo;\n\n#nullable enable\npublic class Alpha { public string? A; }\n#nullable restore\n\npublic class Beta { }\n\npublic class Gamma { public string? G; }\n";

    private const string GeneratedRestoreLost = "// <auto-generated/>\n" + GeneratedBody;

    private const string GeneratedRestoreAdded = "// <auto-generated/>\n#nullable enable warnings\nnamespace Demo;\n\nclass Alpha { }\n\n#nullable restore warnings\nclass Beta { }\n\nclass Gamma { public string? G; void M() { string s = null; } }\n";

    private const string LineMappedBeta = "class Alpha\n{\n#nullable enable\n}\n\n#line 100 \"Mapped.cs\"\nnamespace N\n{\n    class Beta\n    {\n        int Get() => Line();\n        static int Line([System.Runtime.CompilerServices.CallerLineNumber] int line = 0) => line;\n    }\n}\n";

    private const string UsingAfterNullable = "namespace N1\n{\n#nullable enable\n    public class Alpha { public string? A; }\n}\n\nnamespace N2\n{\n    using L = System.Collections.Generic.List<string?>;\n\n    public class Beta { public L? B; }\n}\n";

    private static string UnderIfInAlpha(string lineStart)
    {
        return "namespace N;\n\npublic class Alpha\n{\n#if X" + lineStart + "nullable enable\n#endif\n}\n\npublic class Beta\n{\n    public string? B;\n}\n";
    }

    // The line the compiler embeds for Beta's call, after any #line mapping.
    private static int MappedLineOfCall(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var call = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().Single();
        return tree.GetMappedLineSpan(call.Span).StartLinePosition.Line;
    }

    private static FileResult PlanRow(string source, string name = "Alpha.cs")
    {
        using var ws = new TempWorkspace();
        var alphaPath = ws.WriteFile(name, source);
        var row = Assert.Single(new FileSplitter(new Options(ws.WriteInputList(alphaPath), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null)).Run());
        Assert.Equal("skipped", row.Status);
        return row;
    }
}
