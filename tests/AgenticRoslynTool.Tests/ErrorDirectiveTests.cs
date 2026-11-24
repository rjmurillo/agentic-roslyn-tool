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

        // A second content run reads the split file through the same rule and sees it done.
        var again = new FileSplitter(new Options(ws.WriteInputList(path), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Content, null)).Run();
        Assert.StartsWith("already applied", Assert.Single(again).Reason);
    }

    [Theory]
    // Excused: some enclosing branch depends on a symbol the real build may define.
    [InlineData("#if NET8_0\n#elif NET472\n#else\n#error x\n#endif", null)]
    [InlineData("#if A\n#if B\n#else\n#error x\n#endif\n#endif", null)]
    [InlineData("#if A\n#if true\n#error x\n#endif\n#endif", null)]
    [InlineData("#if !NETFX\n#error version\n#endif", null)]
    // Kept: every build takes this branch, so every build fails.
    [InlineData("#if true\n#error x\n#endif", "CS1029")]
    [InlineData("#if A\n#endif\n#error x", "CS1029")]
    [InlineData("#error version", "CS8304")]
    [InlineData("#endif\n#error x", "CS1029")]
    public void Errors_ExcusesAnErrorDirectiveOnlyWhenItsBranchNamesASymbol(string directives, string? expectedId)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "namespace Sample;\n\ninternal class Host\n{\n" + directives + "\n}\n", SourceParsing.Options);

        var ids = SourceParsing.Errors(tree).Select(d => d.Id).ToArray();

        if (expectedId is null)
        {
            Assert.Empty(ids);
        }
        else
        {
            Assert.Contains(expectedId, ids);
        }
    }

    [Theory]
    // A symbol the file sets with #define or #undef has the same value in every build.
    [InlineData("#define X\nnamespace Sample;\n\ninternal class Host\n{\n#if X\n#error always\n#endif\n}\n", true)]
    [InlineData("#undef X\nnamespace Sample;\n\ninternal class Host\n{\n#if !X\n#error always\n#endif\n}\n", true)]
    [InlineData("#define X\nnamespace Sample;\n\ninternal class Host\n{\n#if X && Y\n#error x\n#endif\n}\n", false)]
    // A #define under a symbol-dependent #if runs only in some builds, so its symbol still
    // depends on the build. The polyfill pattern below compiles in a build that defines NET.
    [InlineData("#if !NET\n#define LEGACY\n#endif\nnamespace Sample;\n\ninternal class Host\n{\n#if LEGACY\n#error legacy not supported\n#endif\n}\n", false)]
    [InlineData("#if !NET\n#undef MODERN\n#endif\nnamespace Sample;\n\ninternal class Host\n{\n#if !MODERN\n#error needs MODERN\n#endif\n}\n", false)]
    // A broken group beside an excused #error is still broken: only the #error's own
    // diagnostics are excused, never a neighbor's.
    [InlineData("namespace Sample;\n\ninternal class Host\n{\n#if X\n#else\n#error need X\n#else\n#endif\n}\n", true)]
    [InlineData("namespace Sample;\n\ninternal class Host\n{\n#if X\n#else\n#error need X\n#elif Y\n#endif\n}\n", true)]
    public void Errors_KnowsSymbolsTheFileSetsAndExcusesOnlyTheErrorDirective(string source, bool expectErrors)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, SourceParsing.Options);

        Assert.Equal(expectErrors, SourceParsing.Errors(tree).Length != 0);
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
