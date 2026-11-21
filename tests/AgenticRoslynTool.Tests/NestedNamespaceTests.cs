using System;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Xunit;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Pins how a type inside a nested block namespace is placed. Each such type is a split
/// candidate of its own and lands in exactly one output, never copied into every output.
/// </summary>
public sealed class NestedNamespaceTests
{
    private const string OuterSource =
        "namespace A\n" +
        "{\n" +
        "    public class Outer\n" +
        "    {\n" +
        "    }\n" +
        "\n" +
        "    namespace B\n" +
        "    {\n" +
        "        public class Deep\n" +
        "        {\n" +
        "        }\n" +
        "    }\n" +
        "\n" +
        "    public class Second\n" +
        "    {\n" +
        "    }\n" +
        "}\n";

    [Fact]
    public void Find_TypeInNestedNamespace_IsFoundInDocumentOrder()
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(OuterSource).GetRoot();

        var keys = TopLevelType.Find(root).Select(t => t.Key).ToArray();

        Assert.Equal(new[] { "Outer", "Deep", "Second" }, keys);
    }

    [Fact]
    public void Split_TypeInNestedNamespace_IsWrittenExactlyOnce()
    {
        using var ws = new TempWorkspace();

        var result = PlanThenContent(ws, "Outer.cs", OuterSource);

        Assert.Equal("split", result.Status);
        Assert.Equal(1, CountDeclarations(ws, "class Deep"));
        Assert.Equal(1, CountDeclarations(ws, "class Outer"));
        Assert.Equal(1, CountDeclarations(ws, "class Second"));
        var deepText = File.ReadAllText(Path.Combine(ws.Root, "Deep.cs"));
        Assert.Contains("namespace B", deepText, StringComparison.Ordinal);
        Assert.Contains("class Deep", deepText, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyOutputs_NestedTypeCopiedIntoTwoOutputs_Throws()
    {
        var tree = CSharpSyntaxTree.ParseText(OuterSource);
        var types = TopLevelType.Find(tree.GetCompilationUnitRoot()).ToArray();
        var outer = "namespace A\n{\n    public class Outer\n    {\n    }\n\n    namespace B\n    {\n        public class Deep\n        {\n        }\n    }\n}\n";
        var second = "namespace A\n{\n\n    namespace B\n    {\n        public class Deep\n        {\n        }\n    }\n\n    public class Second\n    {\n    }\n}\n";
        var outputs = new[]
        {
            new OutputFile("Outer.cs", types[0], outer, outer, Array.Empty<string>()),
            new OutputFile("Second.cs", types[^1], second, second, Array.Empty<string>()),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => OutputVerifier.VerifyOutputs("Outer.cs", types, outputs, CSharpParseOptions.Default, null));

        Assert.Contains("Deep appears 2 times", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_KeptTypeInsideNestedNamespace_MovesOuterTypeOut()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace A\n" +
            "{\n" +
            "    public class Outer { }\n" +
            "\n" +
            "    namespace B\n" +
            "    {\n" +
            "        public class Deep { }\n" +
            "    }\n" +
            "}\n";

        var result = PlanThenContent(ws, "Deep.cs", source);

        Assert.Equal("split", result.Status);
        Assert.Equal(1, CountDeclarations(ws, "class Outer"));
        Assert.Equal(1, CountDeclarations(ws, "class Deep"));
        Assert.Contains("class Deep", File.ReadAllText(Path.Combine(ws.Root, "Deep.cs")), StringComparison.Ordinal);
        Assert.Contains("class Outer", File.ReadAllText(Path.Combine(ws.Root, "Outer.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_SameNameDifferentArityAtTwoDepths_Splits()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace A\n" +
            "{\n" +
            "    public class Foo { }\n" +
            "\n" +
            "    namespace B\n" +
            "    {\n" +
            "        public class Foo<T> { }\n" +
            "    }\n" +
            "}\n";

        var result = PlanThenContent(ws, "Foo.cs", source);

        Assert.Equal("split", result.Status);
        Assert.Contains("class Foo<T>", File.ReadAllText(Path.Combine(ws.Root, "Foo{T}.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("Foo<T>", File.ReadAllText(Path.Combine(ws.Root, "Foo.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_SameArityDifferentTypeParameterNames_SplitsUnderStyleCop()
    {
        using var ws = new TempWorkspace();

        var plan = PlanOnly(ws, "Box.cs", BoxSource, FileNamingConvention.StyleCop);

        Assert.Equal("split", plan.Status);
        Assert.Contains(plan.NewFiles, f => f.Path.EndsWith("Box{U}.cs", StringComparison.Ordinal) && f.Type == "Box<U>");
    }

    [Fact]
    public void Plan_SameArityDifferentTypeParameterNames_CollidesUnderMetadata()
    {
        using var ws = new TempWorkspace();

        var plan = PlanOnly(ws, "Box.cs", BoxSource, FileNamingConvention.Metadata);

        Assert.Equal("skipped", plan.Status);
        Assert.StartsWith("target path collision within split", plan.Reason, StringComparison.Ordinal);
        Assert.EndsWith("Box`1.cs", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Split_DocCommentOnNestedType_TravelsWithIt()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace A\n" +
            "{\n" +
            "    public class Outer { }\n" +
            "\n" +
            "    namespace B\n" +
            "    {\n" +
            "        /// <summary>Deep docs.</summary>\n" +
            "        public class Deep { }\n" +
            "    }\n" +
            "}\n";

        var result = PlanThenContent(ws, "Outer.cs", source);

        Assert.Equal("split", result.Status);
        Assert.Contains("Deep docs.", File.ReadAllText(Path.Combine(ws.Root, "Deep.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("Deep docs.", File.ReadAllText(Path.Combine(ws.Root, "Outer.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RegionSpanningTwoNestedTypes_IsRefused()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace A\n" +
            "{\n" +
            "    public class Outer { }\n" +
            "\n" +
            "#region Both\n" +
            "    namespace B\n" +
            "    {\n" +
            "        public class First { }\n" +
            "\n" +
            "        public class Later { }\n" +
            "    }\n" +
            "#endregion\n" +
            "}\n";

        var path = ws.WriteFile("Outer.cs", source);
        var listPath = ws.WriteInputList(path);
        var plan = Assert.Single(new FileSplitter(new Options(listPath, Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null)).Run());

        Assert.Equal("skipped", plan.Status);
        Assert.EndsWith("crosses top-level type boundaries", plan.Reason, StringComparison.Ordinal);
        Assert.Equal(source, File.ReadAllText(Path.Combine(ws.Root, "Outer.cs")));
        Assert.Single(Directory.GetFiles(ws.Root, "*.cs"));
    }

    [Fact]
    public void Split_OneOuterTypeAndOneNestedType_Splits()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace A\n" +
            "{\n" +
            "    public class Solo { }\n" +
            "\n" +
            "    namespace B\n" +
            "    {\n" +
            "        public class Deep { }\n" +
            "    }\n" +
            "}\n";

        var result = PlanThenContent(ws, "Solo.cs", source);

        Assert.Equal("split", result.Status);
        Assert.Equal(1, CountDeclarations(ws, "class Solo"));
        Assert.Equal(1, CountDeclarations(ws, "class Deep"));
        Assert.Contains("class Deep", File.ReadAllText(Path.Combine(ws.Root, "Deep.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_TwoTypesOnlyInNestedNamespace_Splits()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace A\n" +
            "{\n" +
            "    namespace B\n" +
            "    {\n" +
            "        public class First { }\n" +
            "\n" +
            "        public class Later { }\n" +
            "    }\n" +
            "}\n";

        var result = PlanThenContent(ws, "First.cs", source);

        Assert.Equal("split", result.Status);
        Assert.Equal(1, CountDeclarations(ws, "class First"));
        Assert.Equal(1, CountDeclarations(ws, "class Later"));
        Assert.Contains("namespace B", File.ReadAllText(Path.Combine(ws.Root, "Later.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_TypeThreeNamespacesDown_IsWrittenExactlyOnce()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace A\n" +
            "{\n" +
            "    public class Top { }\n" +
            "\n" +
            "    namespace B\n" +
            "    {\n" +
            "        namespace C\n" +
            "        {\n" +
            "            public class Bottom { }\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        var result = PlanThenContent(ws, "Top.cs", source);

        Assert.Equal("split", result.Status);
        Assert.Equal(1, CountDeclarations(ws, "class Top"));
        Assert.Equal(1, CountDeclarations(ws, "class Bottom"));
        Assert.Contains("namespace C", File.ReadAllText(Path.Combine(ws.Root, "Bottom.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Split_SameNameAtTwoDepths_IsSkippedAndChangesNothing()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace A\n" +
            "{\n" +
            "    public class Same { }\n" +
            "\n" +
            "    public class Other { }\n" +
            "\n" +
            "    namespace B\n" +
            "    {\n" +
            "        public class Same { }\n" +
            "    }\n" +
            "}\n";

        var path = ws.WriteFile("Same.cs", source);
        var listPath = ws.WriteInputList(path);
        var plan = Assert.Single(new FileSplitter(new Options(listPath, Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null)).Run());
        var content = Assert.Single(ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv")));

        Assert.Equal("skipped", plan.Status);
        Assert.StartsWith("target path collision within split", plan.Reason, StringComparison.Ordinal);
        Assert.Equal("skipped", content.Status);
        Assert.Equal(source, File.ReadAllText(Path.Combine(ws.Root, "Same.cs")));
        Assert.Single(Directory.GetFiles(ws.Root, "*.cs"));
    }

    private const string BoxSource =
        "namespace A\n" +
        "{\n" +
        "    public class Box<T> { }\n" +
        "\n" +
        "    public class Other { }\n" +
        "\n" +
        "    namespace B\n" +
        "    {\n" +
        "        public class Box<U> { }\n" +
        "    }\n" +
        "}\n";

    private static FileResult PlanOnly(TempWorkspace ws, string fileName, string source, FileNamingConvention naming)
    {
        var path = ws.WriteFile(fileName, source);
        var listPath = ws.WriteInputList(path);
        var options = new Options(listPath, Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null, FileNaming: naming);
        return Assert.Single(new FileSplitter(options).Run());
    }

    private static FileResult PlanThenContent(TempWorkspace ws, string fileName, string source)
    {
        var path = ws.WriteFile(fileName, source);
        var listPath = ws.WriteInputList(path);
        return Assert.Single(ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv")));
    }

    private static int CountDeclarations(TempWorkspace ws, string declaration)
    {
        return Directory.GetFiles(ws.Root, "*.cs")
            .Sum(file => File.ReadAllText(file).Split('\n').Count(line => line.TrimEnd().EndsWith(declaration, StringComparison.Ordinal) || line.Contains(declaration + " ", StringComparison.Ordinal)));
    }
}
