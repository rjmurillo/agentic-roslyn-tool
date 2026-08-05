using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Exercises the analysis seams directly from a parsed string, with no temporary
/// repository and no disk.
/// </summary>
/// <remarks>
/// These assertions were not expressible before the decomposition: every one of these
/// methods was private to a class that could only be driven end to end through the CLI.
/// They are the evidence for the testability claim, not a replacement for the end-to-end
/// suite, which still owns the behavior contracts.
/// </remarks>
public sealed class SeamUnitTests
{
    private static (CompilationUnitSyntax Root, IReadOnlyList<TopLevelType> Types) Parse(string source)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
        return (root, TopLevelType.Find(root).ToArray());
    }

    [Fact]
    public void AnalyzeDirectiveSafety_RegionSpanningTwoTypes_IsUnsafe()
    {
        var (root, types) = Parse(
            "#region Both\r\nclass A { }\r\nclass B { }\r\n#endregion\r\n");

        var safety = DirectiveAnalyzer.AnalyzeDirectiveSafety(root, types);

        Assert.False(safety.IsSafe);
        Assert.NotNull(safety.Reason);
    }

    [Fact]
    public void AnalyzeDirectiveSafety_RegionInsideOneType_IsSafe()
    {
        var (root, types) = Parse(
            "class A\r\n{\r\n#region Inner\r\n    int x;\r\n#endregion\r\n}\r\nclass B { }\r\n");

        var safety = DirectiveAnalyzer.AnalyzeDirectiveSafety(root, types);

        Assert.True(safety.IsSafe);
        Assert.Null(safety.Reason);
    }

    [Fact]
    public void VerifyLineConservation_OutputMissingATypesLine_Throws()
    {
        var source = "class A\r\n{\r\n    int x;\r\n}\r\nclass B { }\r\n";
        var (_, types) = Parse(source);
        var complete = new OutputFile("A.cs", types[0], "class A\r\n{\r\n    int x;\r\n}\r\n", "class A\r\n{\r\n    int x;\r\n}\r\n");
        var truncated = new OutputFile("B.cs", types[1], "\r\n", "\r\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => OutputVerifier.VerifyLineConservation("in.cs", types, new[] { complete, truncated }));

        Assert.Contains("class B", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyLineConservation_OutputsCoveringEveryLine_DoesNotThrow()
    {
        var source = "class A\r\n{\r\n    int x;\r\n}\r\nclass B { }\r\n";
        var (_, types) = Parse(source);
        var first = new OutputFile("A.cs", types[0], "class A\r\n{\r\n    int x;\r\n}\r\n", "class A\r\n{\r\n    int x;\r\n}\r\n");
        var second = new OutputFile("B.cs", types[1], "class B { }\r\n", "class B { }\r\n");

        OutputVerifier.VerifyLineConservation("in.cs", types, new[] { first, second });
    }

    [Fact]
    public void GetFileName_TwoTypesDifferingOnlyByArity_AreDisambiguated()
    {
        var (_, types) = Parse("class Foo { }\r\nclass Foo<T> { }\r\n");

        var names = types.Select(type => OutputBuilder.GetFileName(type, types)).ToArray();

        Assert.Equal(new[] { "Foo.cs", "Foo{T}.cs" }, names);
    }
}
