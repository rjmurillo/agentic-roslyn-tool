using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The verifier's own check on rendered outputs. The planner refuses these shapes first, so
// a real run never reaches them. Hand-built outputs are the only way to show the check
// fails the row if the planner ever lets one through.
public sealed class FileLocalVerifierTests
{
    private const string ReadPath = "/work/Alpha.cs";

    private const string Prologue = "namespace Sample;\n\n";

    private const string Kept = Prologue + "internal class Alpha\n{\n}\n\nfile class AlphaHelper\n{\n}\n";

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    [Fact]
    public void KeptFileWithItsFileLocalType_AndACleanMovedFile_Pass()
    {
        Verify((ReadPath, Kept), ("/work/Caller.cs", Prologue + "internal class Caller\n{\n}\n"));
    }

    [Fact]
    public void MovedOutputThatNamesAFileLocalType_FailsTheRow()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Verify((ReadPath, Kept), ("/work/Caller.cs", Prologue + "internal class Caller\n{\n    public object Make() => new AlphaHelper();\n}\n")));

        Assert.Equal("/work/Caller.cs names file-local type AlphaHelper, which stays in /work/Alpha.cs", error.Message);
    }

    [Fact]
    public void MovedOutputWhoseUsingNamesAFileLocalType_FailsTheRow()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Verify((ReadPath, Kept), ("/work/Caller.cs", "using Helper = Sample.AlphaHelper;\n\n" + Prologue + "internal class Caller\n{\n}\n")));

        Assert.Equal("/work/Caller.cs names file-local type AlphaHelper, which stays in /work/Alpha.cs", error.Message);
    }

    [Fact]
    public void FileLocalTypeInAMovedOutput_FailsTheRow()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Verify((ReadPath, Prologue + "internal class Alpha\n{\n}\n"), ("/work/Other.cs", Prologue + "file class AlphaHelper\n{\n}\n")));

        Assert.Equal("/work/Other.cs: a file-local type would leave /work/Alpha.cs", error.Message);
    }

    [Fact]
    public void KeptOutputUnderAnotherPath_FailsTheRow()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Verify(("/work/AlphaRenamed.cs", Kept)));

        Assert.Equal("/work/Alpha.cs: a file that declares a file-local type would be renamed", error.Message);
    }

    [Fact]
    public void InputWithNoFileLocalType_IsNotHeldToTheReadPath()
    {
        var input = TopLevelType.Find(CSharpSyntaxTree.ParseText(Prologue + "internal class Alpha\n{\n}\n\ninternal class Beta\n{\n}\n", ParseOptions).GetCompilationUnitRoot()).ToArray();
        var renamed = new OutputFile("/work/AlphaRenamed.cs", input[0], Prologue + "internal class Alpha\n{\n}\n", string.Empty);

        OutputVerifier.VerifyFileLocalTypesStay(ReadPath, input, [renamed], ParseOptions);
    }

    // Asks the check about outputs written for an input that declares Alpha and the
    // file-local AlphaHelper. The check reads only each output's path and text.
    private static void Verify(params (string Path, string Text)[] outputs)
    {
        var input = TopLevelType.Find(CSharpSyntaxTree.ParseText(Kept, ParseOptions, path: ReadPath).GetCompilationUnitRoot()).ToArray();
        var files = outputs.Select(output => new OutputFile(output.Path, input[0], output.Text, output.Text)).ToArray();
        OutputVerifier.VerifyFileLocalTypesStay(ReadPath, input, files, ParseOptions);
    }
}
