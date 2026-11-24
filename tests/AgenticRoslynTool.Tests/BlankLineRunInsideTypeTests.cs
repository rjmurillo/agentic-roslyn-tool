using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A run of three or more blank lines inside a type is part of that type's owned text.
// The blank line collapse used to shorten it, so the ownership check found the type's
// text zero times and the input ended `failed`. Shape taken from Dapper's
// tests/Dapper.Tests/Providers/SqliteTests.cs at 188094784256da61d7a37c43ea54c64213c2ce7c.
public sealed class BlankLineRunInsideTypeTests
{
    private const string Moved =
        "    public class Provider\n" +
        "    {\n" +
        "        public int Id { get; }\n" +
        "    }\n";

    // Three blank lines in a row, the middle one holding only spaces, as in the Dapper file.
    private const string KeptWithRun =
        "    public class Foo : Provider\n" +
        "    {\n" +
        "        public void A()\n" +
        "        {\n" +
        "        }\n" +
        "\n" +
        "        \n" +
        "\n" +
        "        public void B()\n" +
        "        {\n" +
        "        }\n" +
        "    }";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunInsideKeptType_SplitsAndKeepsTheRun(string newLine)
    {
        using var ws = new TempWorkspace();
        var source = Wrap(KeptWithRun + "\n\n" + Moved);
        var result = Split(ws, source, newLine);

        AssertSplit(result);
        Assert.Contains(Lines(KeptWithRun, newLine), File.ReadAllText(Path.Combine(ws.Root, "Foo.cs")));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunInsideMovedType_SplitsAndKeepsTheRun(string newLine)
    {
        var movedWithRun =
            "    public class Bar\n" +
            "    {\n" +
            "        public int X;\n" +
            "\n" +
            "\n" +
            "\n" +
            "\n" +
            "        public int Y;\n" +
            "    }";
        using var ws = new TempWorkspace();
        var source = Wrap("    public class Foo\n    {\n    }\n\n" + movedWithRun + "\n");
        var result = Split(ws, source, newLine);

        AssertSplit(result);
        Assert.Contains(Lines(movedWithRun, newLine), File.ReadAllText(Path.Combine(ws.Root, "Bar.cs")));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunBetweenDocCommentAndDeclaration_IsKept(string newLine)
    {
        // The owned span starts at the doc comment, so blank lines after it are owned text.
        var documented =
            "    /// <summary>Bar.</summary>\n" +
            "\n" +
            "\n" +
            "\n" +
            "    public class Bar\n" +
            "    {\n" +
            "    }";
        using var ws = new TempWorkspace();
        var source = Wrap("    public class Foo\n    {\n    }\n\n" + documented + "\n");
        var result = Split(ws, source, newLine);

        AssertSplit(result);
        Assert.Contains(Lines(documented, newLine), File.ReadAllText(Path.Combine(ws.Root, "Bar.cs")));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void TrailingWhitespaceAfterBrace_IsKept(string newLine)
    {
        // Pins the `{ ` line from the Dapper file. It split before the fix as well.
        var kept =
            "    public class Foo : Provider\n" +
            "    { \n" +
            "        public void A()\n" +
            "        {\n" +
            "        }\n" +
            "    }";
        using var ws = new TempWorkspace();
        var result = Split(ws, Wrap(kept + "\n\n" + Moved), newLine);

        AssertSplit(result);
        Assert.Contains(Lines(kept, newLine), File.ReadAllText(Path.Combine(ws.Root, "Foo.cs")));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunBetweenTypes_StillCollapses(string newLine)
    {
        // A run outside every type's owned span is still tidied to two blank lines.
        using var ws = new TempWorkspace();
        var source = Wrap("    public class Foo\n    {\n    }\n\n\n\n\n    public class Bar\n    {\n    }\n");
        var result = Split(ws, source, newLine);

        AssertSplit(result);
        var barText = File.ReadAllText(Path.Combine(ws.Root, "Bar.cs"));
        Assert.DoesNotContain(newLine + newLine + newLine + newLine, barText);
        Assert.Contains("{" + newLine + newLine + newLine + "    public class Bar", barText);
    }

    private static void AssertSplit(FileResult result)
    {
        Assert.True(result.Status == "split", $"{result.Status}: {result.Reason}");
    }

    private static string Wrap(string body)
    {
        return TestHeader.Copyright + "\n" +
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns\n" +
            "{\n" +
            body +
            "}\n";
    }

    private static string Lines(string text, string newLine)
    {
        return text.Replace("\n", newLine);
    }

    // Runs plan and content separately so a plan time failure reports its own reason,
    // rather than the content phase's "not present as split in plan manifest".
    private static FileResult Split(TempWorkspace ws, string source, string newLine)
    {
        var fooPath = ws.WriteFile("Foo.cs", Lines(source, newLine));
        var listPath = ws.WriteInputList(fooPath);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var plan = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, ws.Root, Phase.Plan, null)).Run());
        AssertSplit(plan);
        ManifestWriter.Write(new[] { plan }, manifestPath, CollisionNaming.FileFirst);
        return Assert.Single(new FileSplitter(new Options(listPath, manifestPath, ws.Root, Phase.Content, null)).Run());
    }
}
