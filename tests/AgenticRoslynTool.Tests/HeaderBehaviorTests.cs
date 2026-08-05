using System;
using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class HeaderBehaviorTests
{
    [Fact]
    public void NoRequiredHeader_HeaderlessSource_SplitsWithoutLeadingBlankLine_AndFirstTypeBlockCommentStaysAttached()
    {
        // Regression pin for the removal of GetHeaderFromFirstType. The old code
        // lifted the first type's leading block comment into a "file header" and
        // copied it into every sibling output, and it emitted a spurious leading
        // blank line. Neither of those must happen now.
        using var ws = new TempWorkspace();
        var fooBlock =
            "/*\n" +
            " * intro for Foo\n" +
            "\n" +
            " * more Foo notes after a blank line\n" +
            " */";
        var source =
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            fooBlock + "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);

        var results = ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv"));
        Assert.Equal("split", Assert.Single(results).Status);

        var fooText = File.ReadAllText(fooPath);
        var barText = File.ReadAllText(Path.Combine(ws.Root, "Bar.cs"));

        Assert.Contains(fooBlock, fooText);
        Assert.DoesNotContain("intro for Foo", barText);
        Assert.DoesNotContain("more Foo notes", barText);

        // No spurious leading blank line on either output.
        Assert.False(fooText.StartsWith("\n", StringComparison.Ordinal) || fooText.StartsWith("\r\n", StringComparison.Ordinal));
        Assert.False(barText.StartsWith("\n", StringComparison.Ordinal) || barText.StartsWith("\r\n", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiredHeader_IsPrependedToEveryOutput_WithOneBlankLine_AndPassesLineConservation()
    {
        using var ws = new TempWorkspace();
        const string required = "// Copyright (c) Contoso.";
        var source =
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);
        var manifest = Path.Combine(ws.Root, "m.csv");

        var planResults = new FileSplitter(new Options(listPath, manifest, ws.Root, Phase.Plan, required)).Run();
        ManifestWriter.Write(planResults, manifest);
        var contentResults = new FileSplitter(new Options(listPath, manifest, ws.Root, Phase.Content, required)).Run();

        // If line conservation were still un-seeded with the header, this would be
        // "failed: non-prologue line was duplicated".
        var result = Assert.Single(contentResults);
        Assert.Equal("split", result.Status);

        foreach (var path in new[] { fooPath, Path.Combine(ws.Root, "Bar.cs") })
        {
            var text = File.ReadAllText(path);
            Assert.StartsWith(required + "\n\n", text);
            // The header appears exactly once at the top, not once per output-of-outputs.
            Assert.Equal(1, CountOccurrences(text, required));
        }
    }

    [Fact]
    public void RequiredHeader_MatchesSourceFileNewlineStyle_Crlf()
    {
        using var ws = new TempWorkspace();
        const string required = "// Copyright (c) Contoso.";
        var source =
            "using System;\r\n" +
            "\r\n" +
            "namespace Sample.Ns;\r\n" +
            "\r\n" +
            "public class Foo { }\r\n" +
            "\r\n" +
            "public class Bar { }\r\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);
        var manifest = Path.Combine(ws.Root, "m.csv");

        var planResults = new FileSplitter(new Options(listPath, manifest, ws.Root, Phase.Plan, required)).Run();
        ManifestWriter.Write(planResults, manifest);
        var contentResults = new FileSplitter(new Options(listPath, manifest, ws.Root, Phase.Content, required)).Run();
        Assert.Equal("split", Assert.Single(contentResults).Status);

        var barText = File.ReadAllText(Path.Combine(ws.Root, "Bar.cs"));
        Assert.StartsWith(required + "\r\n\r\n", barText);

        // The injected header block must not contain a bare LF. Inspect only the
        // header slice so trailing content's line endings are not conflated.
        var headerSlice = barText.Substring(0, required.Length + 4);
        Assert.Equal(required + "\r\n\r\n", headerSlice);
    }

    [Fact]
    public void RequiredHeader_MatchesSourceFileNewlineStyle_Lf()
    {
        using var ws = new TempWorkspace();
        const string required = "// Copyright (c) Contoso.";
        var source =
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);
        var manifest = Path.Combine(ws.Root, "m.csv");

        var planResults = new FileSplitter(new Options(listPath, manifest, ws.Root, Phase.Plan, required)).Run();
        ManifestWriter.Write(planResults, manifest);
        var contentResults = new FileSplitter(new Options(listPath, manifest, ws.Root, Phase.Content, required)).Run();
        Assert.Equal("split", Assert.Single(contentResults).Status);

        var barText = File.ReadAllText(Path.Combine(ws.Root, "Bar.cs"));
        var headerSlice = barText.Substring(0, required.Length + 2);
        Assert.Equal(required + "\n\n", headerSlice);
    }

    [Fact]
    public void RequiredHeader_AlreadyPresentInSource_IsNotDoubled()
    {
        using var ws = new TempWorkspace();
        const string required = "// Copyright (c) Contoso.";
        var source =
            required + "\n" +
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);
        var manifest = Path.Combine(ws.Root, "m.csv");

        var planResults = new FileSplitter(new Options(listPath, manifest, ws.Root, Phase.Plan, required)).Run();
        ManifestWriter.Write(planResults, manifest);
        var contentResults = new FileSplitter(new Options(listPath, manifest, ws.Root, Phase.Content, required)).Run();
        Assert.Equal("split", Assert.Single(contentResults).Status);

        foreach (var path in new[] { fooPath, Path.Combine(ws.Root, "Bar.cs") })
        {
            var text = File.ReadAllText(path);
            Assert.StartsWith(required, text);
            Assert.Equal(1, CountOccurrences(text, required));
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
