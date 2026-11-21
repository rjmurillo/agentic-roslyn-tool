using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The check on rendered outputs. The planner refuses these shapes first, so a real run
// never reaches them. Hand-built outputs are the only way to show the check fails the row
// if the planner ever lets one through.
public sealed class FileLocalVerifierTests
{
    private const string ReadPath = "/work/Alpha.cs";

    private const string Prologue = "namespace Sample;\n\n";

    private const string Kept = Prologue + "internal class Alpha\n{\n}\n\nfile class AlphaHelper\n{\n}\n";

    private const string NamesIt = "/work/Caller.cs names file-local type AlphaHelper, which stays in /work/Alpha.cs";

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    [Fact]
    public void KeptFileWithItsFileLocalType_AndACleanMovedFile_Pass()
    {
        Assert.Null(Describe((ReadPath, Kept), ("/work/Caller.cs", Prologue + "internal class Caller\n{\n}\n")));
    }

    [Theory]
    [InlineData("namespace Sample;\n\ninternal class Caller\n{\n    public object Make() => new AlphaHelper();\n}\n")]
    [InlineData("using Helper = Sample.AlphaHelper;\n\nnamespace Sample;\n\ninternal class Caller\n{\n}\n")]
    [InlineData("namespace Sample\n{\n    using Helper = Sample.AlphaHelper;\n\n    internal class Caller\n    {\n    }\n}\n")]
    [InlineData("namespace Sample\n{\n    internal class Caller\n    {\n    }\n\n    namespace Inner\n    {\n        internal class Deep\n        {\n            public object Make() => new AlphaHelper();\n        }\n    }\n}\n")]
    public void MovedOutputThatNamesAFileLocalType_FailsTheRow(string moved)
    {
        // In a type, in a using directive at either level, or in a nested namespace.
        Assert.Equal(NamesIt, Describe((ReadPath, Kept), ("/work/Caller.cs", moved)));
    }

    [Fact]
    public void FileLocalTypeInAMovedOutput_FailsTheRow()
    {
        var violation = Describe((ReadPath, Prologue + "internal class Alpha\n{\n}\n"), ("/work/Other.cs", Prologue + "file class AlphaHelper\n{\n}\n"));

        Assert.Equal("/work/Other.cs: a file-local type would leave /work/Alpha.cs", violation);
    }

    [Fact]
    public void KeptOutputUnderAnotherPath_FailsTheRow()
    {
        Assert.Equal("/work/Alpha.cs: a file that declares a file-local type would be renamed", Describe(("/work/AlphaRenamed.cs", Kept)));
    }

    [Theory]
    [InlineData("#nullable enable\n", "", "/work/Caller.cs")]
    [InlineData("", "#nullable enable\n", "/work/Alpha.cs")]
    [InlineData("#nullable enable\n", "#nullable enable\n#nullable enable\n", "/work/Caller.cs")]
    public void OutputWithOtherStateDirectivesThanTheInput_FailsTheRow(string keptPrefix, string movedPrefix, string failing)
    {
        // The planner refuses a directive a split would drop. This asks again about the
        // text that would reach disk: the kept file, a moved file, and a directive too many.
        const string Source = "#nullable enable\n" + Kept;
        var input = TopLevelType.Find(CSharpSyntaxTree.ParseText(Source, ParseOptions, path: ReadPath).GetCompilationUnitRoot()).ToArray();
        var keptText = keptPrefix + Kept;
        var movedText = movedPrefix + Prologue + "internal class Caller\n{\n}\n";
        var kept = new OutputFile(ReadPath, input[0], keptText, keptText);
        var moved = new OutputFile("/work/Caller.cs", input[0], movedText, movedText);

        var violation = FileLocalTypes.DescribeViolation(ReadPath, input, [kept, moved], ParseOptions);

        Assert.Equal(failing + " would not hold the same #line, #nullable, #pragma, #define, and #undef directives as /work/Alpha.cs", violation);
    }

    [Fact]
    public void VerifyOutputs_RunsTheCheck_OnAnOtherwiseFaithfulSplit()
    {
        // Every older check passes here: the outputs parse, and no type or line is lost.
        // Only the file-local check can fail this row, so this pins that it is called.
        var (types, outputs) = FileLocalCompiler.BuildUnchecked(
            ReadPath,
            Prologue + "internal class Alpha\n{\n}\n\ninternal class Caller\n{\n    public object Make() => new AlphaHelper();\n}\n\nfile class AlphaHelper\n{\n}\n");

        var error = Assert.Throws<InvalidOperationException>(() => OutputVerifier.VerifyOutputs(ReadPath, types, outputs, ParseOptions, null));

        // The moved path comes from Path.Combine, so its separators follow the platform.
        var moved = Assert.Single(outputs, output => output.Path != ReadPath).Path;
        Assert.Equal($"{moved} names file-local type AlphaHelper, which stays in {ReadPath}", error.Message);
    }

    [Fact]
    public void InputWithNoFileLocalType_IsNotHeldToTheReadPath()
    {
        var input = TopLevelType.Find(CSharpSyntaxTree.ParseText(Prologue + "internal class Alpha\n{\n}\n\ninternal class Beta\n{\n}\n", ParseOptions).GetCompilationUnitRoot()).ToArray();
        var renamed = new OutputFile("/work/AlphaRenamed.cs", input[0], Prologue + "internal class Alpha\n{\n}\n", string.Empty);

        Assert.Null(FileLocalTypes.DescribeViolation(ReadPath, input, [renamed], ParseOptions));
    }

    // Asks the check about outputs written for an input that declares Alpha and the
    // file-local AlphaHelper. The check reads only each output's path and text.
    private static string? Describe(params (string Path, string Text)[] outputs)
    {
        var input = TopLevelType.Find(CSharpSyntaxTree.ParseText(Kept, ParseOptions, path: ReadPath).GetCompilationUnitRoot()).ToArray();
        var files = outputs.Select(output => new OutputFile(output.Path, input[0], output.Text, output.Text)).ToArray();
        return FileLocalTypes.DescribeViolation(ReadPath, input, files, ParseOptions);
    }
}
