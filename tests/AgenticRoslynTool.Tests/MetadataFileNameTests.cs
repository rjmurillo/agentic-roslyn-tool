using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgenticRoslynTool.Tests;

// --file-naming metadata names a generic type's file Name`N.cs, the form SA1649 expects under
// StyleCop's metadata convention. The default convention is pinned beside it.
public sealed class MetadataFileNameTests
{
    [Theory]
    [InlineData("class Foo { }", "Foo.cs")]
    [InlineData("class Foo<T> { }", "Foo`1.cs")]
    [InlineData("class Foo<TKey, TValue> { }", "Foo`2.cs")]
    [InlineData("struct Pair<T> { }", "Pair`1.cs")]
    [InlineData("interface IBox<T> { }", "IBox`1.cs")]
    [InlineData("record Row<T>(T Value);", "Row`1.cs")]
    [InlineData("delegate void Handler<T>(T value);", "Handler`1.cs")]
    [InlineData("delegate void Handler();", "Handler.cs")]
    [InlineData("enum Kind { One }", "Kind.cs")]
    public void GetFileName_Metadata_UsesArityForGenericTypesOnly(string declaration, string expected)
    {
        var types = TopLevelType.Find(CSharpSyntaxTree.ParseText(declaration).GetCompilationUnitRoot()).ToArray();

        Assert.Equal(expected, OutputBuilder.GetFileName(Assert.Single(types), types, FileNamingConvention.Metadata));
    }

    [Fact]
    public void Metadata_NamesAMovedGenericTypeByArity()
    {
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("A.cs", FileNamingRuns.AWithGenericFoo);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var result = Assert.Single(FileNamingRuns.RunPlanThenContent(ws, listPath, manifestPath, FileNamingConvention.Metadata, FileNamingConvention.Metadata));

        Assert.Equal("split", result.Status);
        Assert.Equal(new[] { "A.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Contains("class Foo<T>", File.ReadAllText(Path.Combine(ws.Root, "Foo`1.cs")));
        Assert.DoesNotContain("class Foo<T>", File.ReadAllText(source));
    }

    [Fact]
    public void Default_StillNamesALoneGenericTypeWithoutArity()
    {
        using var ws = new TempWorkspace();
        var listPath = ws.WriteInputList(ws.WriteFile("A.cs", FileNamingRuns.AWithGenericFoo));

        var result = Assert.Single(ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv")));

        Assert.Equal("split", result.Status);
        Assert.Equal(new[] { "A.cs", "Foo.cs" }, FileNamingRuns.CsFiles(ws));
    }
}
