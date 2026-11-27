using Xunit;
using static AgenticRoslynTool.Tests.NullableSplitFixture;

namespace AgenticRoslynTool.Tests;

public sealed class NullableContextTests
{
    [Fact]
    public void NullableEnableAboveSecondType_CarriesToTheThirdTypesFile()
    {
        using var ws = new TempWorkspace();
        var source = Namespace + Alpha +
            "#nullable enable\npublic class Beta { public string? B; }\n\n" +
            "public class Gamma { public string? G; }\n";

        SplitPreserved(ws, source);

        Assert.DoesNotContain("CS8632", NullableContextOracle.DiagnosticIds(Read(ws, "Gamma.cs")));
        Assert.Equal(1, CountLine(Read(ws, "Beta.cs"), "#nullable enable"));
        Assert.Equal(0, CountLine(Read(ws, "Alpha.cs"), "#nullable enable"));
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
        var source = prologue + Namespace + Alpha + directive + "\npublic class Beta { }\n\npublic class Gamma { }\n";

        SplitPreserved(ws, source);

        Assert.Contains(expected + "\npublic class Gamma { }\n", Read(ws, "Gamma.cs"));
        Assert.Equal(1, CountLine(Read(ws, "Gamma.cs"), expected));
    }

    [Fact]
    public void KeptTypeAfterTheDirective_RegainsItWhenTheTypeAboveMoves()
    {
        using var ws = new TempWorkspace();
        var source = Namespace +
            "#nullable enable\npublic class Bar { public string? B; }\n\n" +
            "public class Alpha { public string? A; }\n";

        SplitPreserved(ws, source);

        Assert.DoesNotContain("CS8632", NullableContextOracle.DiagnosticIds(Read(ws, "Alpha.cs")));
    }

    [Fact]
    public void DirectiveInsideARemovedTypesBody_IsRestatedForTheTypeBelow()
    {
        using var ws = new TempWorkspace();
        var source = Namespace +
            "public class Alpha\n{\n#nullable enable\n    public string? A;\n}\n\n" +
            "public class Beta { public string? B; }\n";

        SplitPreserved(ws, source);

        Assert.Equal(1, CountLine(Read(ws, "Alpha.cs"), "#nullable enable"));
        Assert.Contains("#nullable enable\npublic class Beta", Read(ws, "Beta.cs"));
    }

    [Fact]
    public void AddedDirective_GoesAboveTheDocCommentAndAttribute()
    {
        using var ws = new TempWorkspace();
        var gamma = "// Gamma comment\n/// <summary>Gamma.</summary>\n[System.Obsolete]\npublic class Gamma { public string? G; }\n";
        var source = Namespace + Alpha + "#nullable enable\npublic class Beta { }\n\n" + gamma;

        SplitPreserved(ws, source);

        Assert.Contains("\n#nullable enable\n" + gamma, Read(ws, "Gamma.cs"));
    }

    [Fact]
    public void BlockNamespaceWithCrLfAndHeader_KeepsNewlinesIndentationAndHeader()
    {
        using var ws = new TempWorkspace();
        var source =
            "// Header line\r\n\r\nnamespace Sample.Ns\r\n{\r\n    public class Alpha { }\r\n\r\n" +
            "#nullable enable\r\n    public class Beta { public string? B; }\r\n\r\n" +
            "    public class Gamma { public string? G; }\r\n}\r\n";

        SplitPreserved(ws, source, "// Header line");

        var gamma = Read(ws, "Gamma.cs");
        Assert.StartsWith("// Header line\r\n", gamma);
        Assert.Contains("\r\n\r\n#nullable enable\r\n    public class Gamma", gamma);
        Assert.DoesNotContain("\n", gamma.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void RequiredHeaderWithANullableLine_StillSplits()
    {
        using var ws = new TempWorkspace();
        var source = Namespace + Alpha + "public class Beta { }\n";

        var row = Run(ws, source, "// Banner\n#nullable enable");

        Assert.Equal("split", row.Status);
        Assert.StartsWith("// Banner\n#nullable enable\n\n" + Namespace, Read(ws, "Beta.cs"));
    }

    [Fact]
    public void BannerAboveAMovedType_StaysFirstAndIsNotRepeated()
    {
        // Beta's own banner opens Beta.cs. A restated line above it hid the banner from the
        // header check, which then wrote a second copy.
        using var ws = new TempWorkspace();
        var source = "class Alpha\n{\n    string? a;\n#nullable enable\n}\n\n// Copyright X\nclass Beta { string? b; }\n";

        SplitPreserved(ws, source, "// Copyright X");

        Assert.Equal("// Copyright X\n#nullable enable\nclass Beta { string? b; }\n", Read(ws, "Beta.cs"));
    }

    [Fact]
    public void UnicodeLineSeparatorAboveAMovedType_StillSplits()
    {
        // The compiler reads U+2028 as a line break. A verifier that split lines on CR and
        // LF alone joined the restated line to it and refused a correct split.
        using var ws = new TempWorkspace();
        var source = "class Alpha\n{\n#nullable enable\n}\n" + (char)0x2028 + "class Beta { string? b; }\n";

        SplitPreserved(ws, source);

        Assert.DoesNotContain("CS8632", NullableContextOracle.DiagnosticIds(Read(ws, "Beta.cs")));
    }

    [Fact]
    public void LoneCarriageReturnFile_RestatesWithoutALineFeed()
    {
        // A file with only lone carriage returns once got a line feed on the restated line,
        // and lost its final carriage return in every output.
        using var ws = new TempWorkspace();
        var source = "#nullable enable\rclass Alpha { }\rclass Beta {\r    public string? Value = null;\r}\r";

        SplitPreserved(ws, source);

        Assert.DoesNotContain((byte)'\n', File.ReadAllBytes(Path.Combine(ws.Root, "Beta.cs")));
        Assert.Equal("#nullable enable\rclass Beta {\r    public string? Value = null;\r}\r", Read(ws, "Beta.cs"));
        Assert.Equal("#nullable enable\rclass Alpha { }\r", Read(ws, "Alpha.cs"));
    }

    [Fact]
    public void OracleSymbols_IncludeAConditionACommentHidesWithNoSymbols()
    {
        // With no symbols, the comment opened under #if !A hides #if B. With A defined, the
        // comment is gone and B is a live condition, so the oracle must also check A with B.
        var source = "#if !A\n/*\n#endif\n#if B\nclass X { }\n#endif\n#if !A\n*/\n#endif\n";
        var parse = Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default;

        Assert.Contains(PreprocessorSymbolSets.Of(source, parse), set => set.SequenceEqual(["A", "B"]));
    }

    [Fact]
    public void DirectiveAboveTheNamespace_IsNotAddedAgain()
    {
        using var ws = new TempWorkspace();
        var source = "#nullable enable\n" + Namespace + "public class Alpha { public string? A; }\n\npublic class Beta { public string? B; }\n";

        SplitPreserved(ws, source);

        Assert.Equal("#nullable enable\n" + Namespace + "public class Beta { public string? B; }\n", Read(ws, "Beta.cs"));
        Assert.Equal(1, CountLine(Read(ws, "Alpha.cs"), "#nullable enable"));
    }
}
