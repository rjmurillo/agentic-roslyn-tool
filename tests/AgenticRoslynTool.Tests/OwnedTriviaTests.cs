using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class OwnedTriviaTests
{
    [Fact]
    public void XmlDoc_Attribute_And_LineComment_MoveWithNonPrimaryType()
    {
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "// ordinary comment for Bar\n" +
            "/// <summary>Bar is a good type.</summary>\n" +
            "[Obsolete(\"use Baz\")]\n" +
            "public class Bar { }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);

        var results = ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv"));
        Assert.Equal("split", Assert.Single(results).Status);

        var barText = File.ReadAllText(Path.Combine(ws.Root, "Bar.cs"));
        var fooText = File.ReadAllText(fooPath);

        Assert.Contains("// ordinary comment for Bar", barText);
        Assert.Contains("/// <summary>Bar is a good type.</summary>", barText);
        Assert.Contains("[Obsolete(\"use Baz\")]", barText);

        Assert.DoesNotContain("// ordinary comment for Bar", fooText);
        Assert.DoesNotContain("<summary>Bar is a good type.</summary>", fooText);
        Assert.DoesNotContain("[Obsolete(\"use Baz\")]", fooText);
    }

    [Fact]
    public void BlockCommentContainingBlankLines_SurvivesByteForByte()
    {
        // A text-level blank-line normalizer would collapse the blank lines inside
        // this block comment. The trivia-level collapser must leave the token text alone.
        using var ws = new TempWorkspace();
        var block =
            "/*\n" +
            " * intro\n" +
            "\n" +
            "\n" +
            " * more, after two blank lines inside the comment\n" +
            " */";
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            block + "\n" +
            "public class Bar { }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);

        var results = ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv"));
        Assert.Equal("split", Assert.Single(results).Status);

        var barText = File.ReadAllText(Path.Combine(ws.Root, "Bar.cs"));
        Assert.Contains(block, barText);
    }
}
