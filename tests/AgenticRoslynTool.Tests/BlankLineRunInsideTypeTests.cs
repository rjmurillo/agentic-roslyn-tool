using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A run of three or more blank lines inside a type is part of that type's owned text.
// The blank line collapse used to shorten it, so the ownership check found the type's
// text zero times and the input ended `failed`. Shape taken from Dapper's
// tests/Dapper.Tests/Providers/SqliteTests.cs at 188094784256da61d7a37c43ea54c64213c2ce7c.
public sealed class BlankLineRunInsideTypeTests
{
    private const string EmptyFoo = "    public class Foo\n    {\n    }\n";

    private const string Provider = "    public class Provider\n    {\n        public int Id { get; }\n    }\n";

    // Three blank lines in a row, the middle one holding only spaces, as in the Dapper file.
    private const string KeptWithRun =
        "    public class Foo : Provider\n" +
        "    {\n" +
        "        public void A() { }\n" +
        "\n        \n\n" +
        "        public void B() { }\n" +
        "    }";

    // Four blank lines in a row, the second holding a tab and two spaces.
    private const string MovedWithRun =
        "    public class Bar\n" +
        "    {\n" +
        "        public int X;\n" +
        "\n\t  \n\n\n" +
        "        public int Y;\n" +
        "    }";

    // The owned span starts at the doc comment, so the run below it is owned text.
    private const string Documented = "    /// <summary>Bar.</summary>\n\n\n\n    public class Bar\n    {\n    }";

    // An attribute list is part of the declaration, so the run below it is owned text.
    private const string Attributed = "[Serializable]\n\n\n\npublic class Bar\n{\n}";

    // Pins the `{ ` line from the Dapper file. It split before the fix as well.
    private const string BraceWithSpace = "    public class Foo : Provider\n    { \n        public void A() { }\n    }";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunInsideKeptType_SplitsAndKeepsTheRun(string newLine)
    {
        Assert.Contains(Lines(KeptWithRun, newLine), SplitAndRead(Wrap(KeptWithRun + "\n\n" + Provider), "Foo.cs", newLine));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunInsideMovedType_SplitsAndKeepsTheRun(string newLine)
    {
        Assert.Contains(Lines(MovedWithRun, newLine), SplitAndRead(Wrap(EmptyFoo + "\n" + MovedWithRun + "\n"), "Bar.cs", newLine));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunBetweenDocCommentAndDeclaration_IsKept(string newLine)
    {
        // The run above the doc comment is outside the owned span and still collapses to two.
        var barText = SplitAndRead(Wrap(EmptyFoo + "\n\n\n\n" + Documented + "\n"), "Bar.cs", newLine);
        Assert.Contains(Lines("{\n\n\n" + Documented, newLine), barText);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunAfterAttributeInFileScopedNamespace_IsKept(string newLine)
    {
        var source = TestHeader.Copyright + "\nusing System;\n\nnamespace Sample.Ns;\n\npublic class Foo\n{\n}\n\n" + Attributed + "\n";
        Assert.Contains(Lines(Attributed, newLine), SplitAndRead(source, "Bar.cs", newLine));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void TrailingWhitespaceAfterBrace_IsKept(string newLine)
    {
        Assert.Contains(Lines(BraceWithSpace, newLine), SplitAndRead(Wrap(BraceWithSpace + "\n\n" + Provider), "Foo.cs", newLine));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void BlankLineRunBetweenTypes_StillCollapses(string newLine)
    {
        // A run outside every type's owned span is still tidied to two blank lines.
        var barText = SplitAndRead(Wrap(EmptyFoo + "\n\n\n\n" + "    public class Bar\n    {\n    }\n"), "Bar.cs", newLine);
        Assert.DoesNotContain(Lines("\n\n\n\n", newLine), barText);
        Assert.Contains(Lines("{\n\n\n    public class Bar", newLine), barText);
    }

    private static string Wrap(string body)
    {
        return TestHeader.Copyright + "\nusing System;\n\nnamespace Sample.Ns\n{\n" + body + "}\n";
    }

    private static string Lines(string text, string newLine)
    {
        return text.Replace("\n", newLine);
    }

    // Runs plan and content separately so a plan time failure reports its own reason,
    // rather than the content phase's "not present as split in plan manifest". Returns
    // the text of one output file.
    private static string SplitAndRead(string source, string outputName, string newLine)
    {
        using var ws = new TempWorkspace();
        var fooPath = ws.WriteFile("Foo.cs", Lines(source, newLine));
        var listPath = ws.WriteInputList(fooPath);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var plan = Assert.Single(new FileSplitter(new Options(listPath, manifestPath, ws.Root, Phase.Plan, null)).Run());
        AssertSplit(plan);
        ManifestWriter.Write(new[] { plan }, manifestPath, CollisionNaming.FileFirst);
        AssertSplit(Assert.Single(new FileSplitter(new Options(listPath, manifestPath, ws.Root, Phase.Content, null)).Run()));
        return File.ReadAllText(Path.Combine(ws.Root, outputName));
    }

    private static void AssertSplit(FileResult result)
    {
        Assert.True(result.Status == "split", $"{result.Status}: {result.Reason}");
    }
}
