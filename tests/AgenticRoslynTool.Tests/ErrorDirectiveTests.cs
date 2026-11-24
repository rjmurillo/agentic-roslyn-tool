using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The tool parses with no preprocessor symbols defined, so the #else branch of a
// target framework check is the active one. A #error there fires CS1029, an error
// diagnostic, although the real build never takes that branch. Version 0.2.0 marked
// such files failed. xunit has eight of them. Decision 33.
public sealed class ErrorDirectiveTests
{
    private const string FrameworkGuardedError =
        "namespace Sample;\n" +
        "\n" +
        "internal class Runner\n" +
        "{\n" +
        "#if NET8_0_OR_GREATER\n" +
        "    public string Target => \"net8\";\n" +
        "#elif NET472\n" +
        "    public string Target => \"net472\";\n" +
        "#else\n" +
        "#error Unknown target framework\n" +
        "#endif\n" +
        "}\n" +
        "\n" +
        "internal class Moved { }\n";

    [Fact]
    public void ErrorDirective_InsideAConditional_Splits()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("Runner.cs", FrameworkGuardedError);

        var results = ws.RunPlanThenContent(ws.WriteInputList(path), Path.Combine(ws.Root, "m.csv"));

        var result = Assert.Single(results);
        Assert.Equal("split", result.Status);
        // The #error travels with the type it sits in, and the output still verifies.
        Assert.Contains("#error Unknown target framework", File.ReadAllText(path));
        Assert.Equal("namespace Sample;\n\ninternal class Moved { }\n", File.ReadAllText(Path.Combine(ws.Root, "Moved.cs")));
    }

    [Fact]
    public void ErrorDirective_OutsideAnyConditional_StillFails()
    {
        // An unconditional #error fails every build of the file. That input is not valid
        // C#, so it keeps the failed status the contract gives a file that does not parse.
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("Broken.cs",
            "#error This file is not finished\n" +
            "namespace Sample;\n" +
            "\n" +
            "internal class Broken { }\n" +
            "internal class Other { }\n");
        var options = new Options(ws.WriteInputList(path), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null);

        var result = Assert.Single(new FileSplitter(options).Run());

        Assert.Equal("failed", result.Status);
        Assert.StartsWith("input has syntax errors: CS1029", result.Reason);
    }

    [Fact]
    public void Errors_IgnoresOnlyTheConditionalErrorDirective()
    {
        // A real syntax error next to a guarded #error is still reported.
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            FrameworkGuardedError + "internal class Bad {\n", SourceParsing.Options);

        var ids = SourceParsing.Errors(tree).Select(d => d.Id).ToArray();

        Assert.DoesNotContain("CS1029", ids);
        Assert.Contains("CS1513", ids);
    }
}
