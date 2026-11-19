using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// --file-naming chooses the SA1649 convention for generic file names. These tests pin how
// the flag is parsed and that it reaches the planner through the real command line.
public sealed class FileNamingOptionTests
{
    [Fact]
    public void Parse_DefaultsToStyleCop()
    {
        var options = Options.Parse(["--input", "list.txt"]);

        Assert.Equal(FileNamingConvention.StyleCop, options.FileNaming);
    }

    [Theory]
    [InlineData("metadata", FileNamingConvention.Metadata)]
    [InlineData("Metadata", FileNamingConvention.Metadata)]
    [InlineData("stylecop", FileNamingConvention.StyleCop)]
    public void Parse_ReadsTheConvention(string value, object expected)
    {
        var options = Options.Parse(["--input", "list.txt", "--file-naming", value]);

        Assert.Equal((FileNamingConvention)expected, options.FileNaming);
    }

    [Fact]
    public void Parse_RejectsAnUnknownConvention()
    {
        var error = Assert.Throws<ArgumentException>(() => Options.Parse(["--input", "list.txt", "--file-naming", "pascal"]));

        Assert.Contains("Unknown file naming convention: pascal", error.Message);
    }

    [Fact]
    public void HasMetaOption_TreatsTheConventionValueAsAValue()
    {
        Assert.False(Options.HasMetaOption(["--file-naming", "--help"], "--help"));
    }

    [Fact]
    public void CommandLine_RejectsAnUnknownMissingOrEmptyConvention()
    {
        var unknown = FileNamingRuns.RunTool("split-types", "--input", "list.txt", "--file-naming", "pascal");
        var missing = FileNamingRuns.RunTool("split-types", "--input", "list.txt", "--file-naming");
        var empty = FileNamingRuns.RunTool("split-types", "--input", "list.txt", "--file-naming", string.Empty);

        Assert.Equal(2, unknown.ExitCode);
        Assert.Equal(string.Empty, unknown.StandardOutput.Trim());
        Assert.Contains("error: Unknown file naming convention: pascal", unknown.StandardError, StringComparison.Ordinal);
        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("error: --file-naming requires a value.", missing.StandardError, StringComparison.Ordinal);
        Assert.Equal(2, empty.ExitCode);
        Assert.Contains("error: Unknown file naming convention", empty.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandLine_Metadata_PlansAnArityFileName()
    {
        // The flag has to reach the planner through the real command line, not only
        // through an Options value a test built by hand.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("A.cs", FileNamingRuns.AWithGenericFoo);
        var manifest = Path.Combine(ws.Root, "manifest.csv");

        var result = FileNamingRuns.RunTool("split-types", "--input", source, "--manifest", manifest, "--dry-run", "--json", "--file-naming", "metadata");

        Assert.Equal(0, result.ExitCode);

        // The JSON writer escapes a backtick as \u0060, so the raw text never holds the
        // file name. Read it from the parsed document, as a caller would.
        using var document = JsonDocument.Parse(result.StandardOutput);
        var row = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
        var newFile = Assert.Single(row.GetProperty("newFiles").EnumerateArray());
        Assert.Equal(Path.Combine(ws.Root, "Foo`1.cs"), newFile.GetProperty("newFilePath").GetString());
        Assert.Contains("Foo`1.cs", File.ReadAllText(manifest), StringComparison.Ordinal);
        Assert.Equal(new[] { "A.cs" }, FileNamingRuns.CsFiles(ws));
    }
}
