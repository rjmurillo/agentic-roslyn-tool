using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class NullableDirectivesTests
{
    // The directive sits inside B's body, so its line is not prologue and may appear only once.
    private const string BodyB = "class B\n{\n#nullable enable\n}\n";

    private const string Source = "class A { }\n" + BodyB + "class C { }\n";

    private static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    [Fact]
    public void LineConservation_SubtractsAnAddedLine_SoItIsNotADuplicate()
    {
        var types = Types(Source);
        var outputs = Outputs(types, "#nullable enable\nclass C { }\n", new[] { "#nullable enable" });

        OutputVerifier.VerifyLineConservation("in.cs", types, outputs);
    }

    [Fact]
    public void LineConservation_RefusesTheSameLineWhenItIsNotDeclaredAdded()
    {
        var types = Types(Source);
        var outputs = Outputs(types, "#nullable enable\nclass C { }\n", Array.Empty<string>());

        var ex = Assert.Throws<InvalidOperationException>(() => OutputVerifier.VerifyLineConservation("in.cs", types, outputs));
        Assert.Contains("non-prologue line was duplicated: #nullable enable", ex.Message);
    }

    [Fact]
    public void LineConservation_RefusesAnAddedLineTheOutputDoesNotHold()
    {
        var types = Types(Source);
        var outputs = Outputs(types, "class C { }\n", new[] { "#nullable enable" });

        var ex = Assert.Throws<InvalidOperationException>(() => OutputVerifier.VerifyLineConservation("in.cs", types, outputs));
        Assert.Contains("added line is missing from the output: #nullable enable", ex.Message);
    }

    [Fact]
    public void NullableContextCheck_RefusesAnOutputThatLostTheContext()
    {
        var types = Types(Source);
        var outputs = Outputs(types, "class C { }\n", Array.Empty<string>());

        var ex = Assert.Throws<InvalidOperationException>(() => OutputVerifier.VerifyNullableContext("in.cs", outputs, Parse));
        Assert.Contains("nullable context for C is annotations restore, warnings restore in C.cs, but annotations enable, warnings enable in the source", ex.Message);
    }

    [Fact]
    public void LineConservation_RefusesAnAddedLineThatIsNotANullableDirective()
    {
        var types = Types(Source);
        var outputs = Outputs(types, "class A { }\n#nullable enable\nclass C { }\n", new[] { "class A { }" });

        var ex = Assert.Throws<InvalidOperationException>(() => OutputVerifier.VerifyLineConservation("in.cs", types, outputs));
        Assert.Contains("added line is not a #nullable directive: class A { }", ex.Message);
    }

    [Theory]
    [InlineData("#nullable enable\n", 0)]
    [InlineData("#if X\n#nullable enable\n#endif\n", 1)]
    [InlineData("#if X\n#nullable disable\n#else\n#nullable enable\n#endif\n", 2)]
    [InlineData("#if X\n  #nullable enable\n#nullable disable\n#endif\n", 2)]
    public void CountBefore_CountsNullableDirectivesUnderIf(string directives, int expected)
    {
        var root = CSharpSyntaxTree.ParseText(directives + "class A { }\n", Parse).GetCompilationUnitRoot();
        var type = TopLevelType.Find(root).Single();

        Assert.Equal(expected, ConditionalNullable.CountBefore(root, type.Node.SpanStart));
    }

    [Theory]
    [InlineData("#nullable enable\n#nullable disable warnings\n", true, false)]
    [InlineData("#nullable enable annotations\n", true, null)]
    [InlineData("#nullable disable\n#nullable restore warnings\n", false, null)]
    [InlineData("#if UNDEFINED\n#nullable enable\n#endif\n", null, null)]
    public void StateAt_FoldsActiveDirectivesInOrder(string directives, bool? annotations, bool? warnings)
    {
        var root = CSharpSyntaxTree.ParseText(directives + "class A { }\n", Parse).GetCompilationUnitRoot();
        var type = TopLevelType.Find(root).Single();

        Assert.Equal((annotations, warnings), NullableDirectives.StateAt(root, type.Node.SpanStart));
    }

    [Theory]
    [InlineData(null, null, true, true, "#nullable enable")]
    [InlineData(true, true, true, false, "#nullable disable warnings")]
    [InlineData(null, null, true, false, "#nullable enable annotations|#nullable disable warnings")]
    [InlineData(true, true, null, null, "#nullable restore")]
    [InlineData(false, true, false, true, "")]
    public void DirectivesBetween_WritesTheFewestLines(bool? fromA, bool? fromW, bool? toA, bool? toW, string expected)
    {
        var lines = NullableDirectives.DirectivesBetween((fromA, fromW), (toA, toW));

        Assert.Equal(expected, string.Join("|", lines));
    }

    private static TopLevelType[] Types(string source)
    {
        return TopLevelType.Find(CSharpSyntaxTree.ParseText(source, Parse).GetCompilationUnitRoot()).ToArray();
    }

    private static OutputFile[] Outputs(TopLevelType[] types, string third, string[] added)
    {
        return new[]
        {
            new OutputFile("A.cs", types[0], "class A { }\n", "class A { }\n", Array.Empty<string>()),
            new OutputFile("B.cs", types[1], BodyB, BodyB, Array.Empty<string>()),
            new OutputFile("C.cs", types[2], third, third, added),
        };
    }
}
