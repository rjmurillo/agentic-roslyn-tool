using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class NullableContextTests
{
    [Fact]
    public void NullableEnableAboveSecondType_CarriesToTheThirdTypesFile()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Alpha { }\n" +
            "\n" +
            "#nullable enable\n" +
            "public class Beta { public string? B; }\n" +
            "\n" +
            "public class Gamma { public string? G; }\n";

        SplitThree(ws, source);

        Assert.DoesNotContain("CS8632", NullableContextOracle.DiagnosticIds(Read(ws, "Gamma.cs")));
        Assert.Equal(1, CountLine(Read(ws, "Beta.cs"), "#nullable enable"));
        Assert.Equal(0, CountLine(Read(ws, "Alpha.cs"), "#nullable enable"));
        NullableContextOracle.AssertPreserved(source, ws.Root);
    }

    [Theory]
    [InlineData("", "#nullable disable", "#nullable disable")]
    [InlineData("#nullable enable\n", "#nullable disable", "#nullable disable")]
    [InlineData("#nullable enable\n", "#nullable restore", "#nullable restore")]
    [InlineData("", "#nullable enable annotations", "#nullable enable annotations")]
    [InlineData("#nullable enable\n", "#nullable disable warnings", "#nullable disable warnings")]
    [InlineData("#nullable enable\n", "#nullable restore annotations", "#nullable restore annotations")]
    public void SettingAboveSecondType_IsRestatedAboveTheThirdType(string prologue, string directive, string expected)
    {
        using var ws = new TempWorkspace();
        var source =
            prologue +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Alpha { }\n" +
            "\n" +
            directive + "\n" +
            "public class Beta { }\n" +
            "\n" +
            "public class Gamma { }\n";

        SplitThree(ws, source);

        Assert.Contains(expected + "\npublic class Gamma { }\n", Read(ws, "Gamma.cs"));
        Assert.Equal(1, CountLine(Read(ws, "Gamma.cs"), expected));
        NullableContextOracle.AssertPreserved(source, ws.Root);
    }

    [Fact]
    public void KeptTypeAfterTheDirective_RegainsItWhenTheTypeAboveMoves()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace Sample.Ns;\n" +
            "\n" +
            "#nullable enable\n" +
            "public class Bar { public string? B; }\n" +
            "\n" +
            "public class Foo { public string? F; }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);

        var results = ws.RunPlanThenContent(ws.WriteInputList(fooPath), Path.Combine(ws.Root, "m.csv"));

        Assert.Equal("split", Assert.Single(results).Status);
        Assert.DoesNotContain("CS8632", NullableContextOracle.DiagnosticIds(File.ReadAllText(fooPath)));
        NullableContextOracle.AssertPreserved(source, ws.Root);
    }

    [Fact]
    public void DirectiveInsideARemovedTypesBody_IsRestatedForTheTypeBelow()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Alpha\n" +
            "{\n" +
            "#nullable enable\n" +
            "    public string? A;\n" +
            "}\n" +
            "\n" +
            "public class Beta { public string? B; }\n" +
            "\n" +
            "public class Gamma { }\n";

        SplitThree(ws, source);

        Assert.Equal(1, CountLine(Read(ws, "Alpha.cs"), "#nullable enable"));
        Assert.Contains("#nullable enable\npublic class Beta", Read(ws, "Beta.cs"));
        NullableContextOracle.AssertPreserved(source, ws.Root);
    }

    [Fact]
    public void AddedDirective_GoesAboveTheDocCommentAndAttribute()
    {
        using var ws = new TempWorkspace();
        var source =
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Alpha { }\n" +
            "\n" +
            "#nullable enable\n" +
            "public class Beta { }\n" +
            "\n" +
            "// Gamma comment\n" +
            "/// <summary>Gamma.</summary>\n" +
            "[System.Obsolete]\n" +
            "public class Gamma { public string? G; }\n";

        SplitThree(ws, source);

        Assert.Contains("\n#nullable enable\n// Gamma comment\n/// <summary>Gamma.</summary>\n[System.Obsolete]\npublic class Gamma", Read(ws, "Gamma.cs"));
        NullableContextOracle.AssertPreserved(source, ws.Root);
    }

    [Fact]
    public void BlockNamespaceWithCrLfAndHeader_KeepsNewlinesIndentationAndHeader()
    {
        using var ws = new TempWorkspace();
        var source =
            "// Header line\r\n" +
            "\r\n" +
            "namespace Sample.Ns\r\n" +
            "{\r\n" +
            "    public class Alpha { }\r\n" +
            "\r\n" +
            "#nullable enable\r\n" +
            "    public class Beta { public string? B; }\r\n" +
            "\r\n" +
            "    public class Gamma { public string? G; }\r\n" +
            "}\r\n";

        SplitThree(ws, source, "// Header line");

        var gamma = Read(ws, "Gamma.cs");
        Assert.StartsWith("// Header line\r\n", gamma);
        Assert.Contains("\r\n\r\n#nullable enable\r\n    public class Gamma", gamma);
        Assert.DoesNotContain("\n", gamma.Replace("\r\n", string.Empty));
        NullableContextOracle.AssertPreserved(source, ws.Root);
    }

    [Fact]
    public void DirectiveAboveTheNamespace_IsNotAddedAgain()
    {
        using var ws = new TempWorkspace();
        var source =
            "#nullable enable\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Alpha { public string? A; }\n" +
            "\n" +
            "public class Beta { public string? B; }\n";
        var alphaPath = ws.WriteFile("Alpha.cs", source);

        var results = ws.RunPlanThenContent(ws.WriteInputList(alphaPath), Path.Combine(ws.Root, "m.csv"));

        Assert.Equal("split", Assert.Single(results).Status);
        Assert.Equal(
            "#nullable enable\nnamespace Sample.Ns;\n\npublic class Beta { public string? B; }\n",
            Read(ws, "Beta.cs"));
        Assert.Equal(1, CountLine(File.ReadAllText(alphaPath), "#nullable enable"));
    }

    [Fact]
    public void DirectiveUnderAnUndefinedSymbol_IsNotRestated_KnownIssue()
    {
        // Known issue: the tool parses with no preprocessor symbols, so a #nullable inside
        // an #if branch that is off in that parse sets nothing. A build that defines the
        // symbol gives Beta an enabled context in the source and not in Beta.cs.
        using var ws = new TempWorkspace();
        var source =
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Alpha\n" +
            "{\n" +
            "#if NULLABLE_ON\n" +
            "#nullable enable\n" +
            "#endif\n" +
            "}\n" +
            "\n" +
            "public class Beta { }\n";
        var alphaPath = ws.WriteFile("Alpha.cs", source);

        var results = ws.RunPlanThenContent(ws.WriteInputList(alphaPath), Path.Combine(ws.Root, "m.csv"));

        Assert.Equal("split", Assert.Single(results).Status);
        Assert.DoesNotContain("#nullable", Read(ws, "Beta.cs"));
    }

    private static void SplitThree(TempWorkspace ws, string source, string? requiredHeader = null)
    {
        var alphaPath = ws.WriteFile("Alpha.cs", source);
        var results = ws.RunPlanThenContent(ws.WriteInputList(alphaPath), Path.Combine(ws.Root, "m.csv"), requiredHeader);
        var row = Assert.Single(results);
        Assert.True(row.Status == "split", $"{row.Status}: {row.Reason}");
    }

    private static string Read(TempWorkspace ws, string name)
    {
        return File.ReadAllText(Path.Combine(ws.Root, name));
    }

    private static int CountLine(string text, string line)
    {
        return text.Replace("\r\n", "\n").Split('\n').Count(l => l == line);
    }
}
