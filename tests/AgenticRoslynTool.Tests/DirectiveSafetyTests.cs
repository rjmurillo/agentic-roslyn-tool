using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class DirectiveSafetyTests
{
    [Fact]
    public void RegionDirective_SpanningTwoTopLevelTypes_IsRefused()
    {
        // The rule pinned here is the one in FileSplitter.AnalyzeDirectiveSafety /
        // GetDirectiveGroupOwner: a directive group that overlaps more than one
        // top-level type and does not sit inside exactly one is rejected. It is not
        // a blanket refusal of every preprocessor directive.
        //
        // #region is used instead of #if because a preprocessor conditional would
        // change which types the parser sees as active. #region does not.
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "#region Group\n" +
            "public class Bar { }\n" +
            "\n" +
            "public class Baz { }\n" +
            "#endregion\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);

        var options = new Options(listPath, Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null);
        var results = new FileSplitter(options).Run();
        var result = Assert.Single(results);
        Assert.Equal("skipped", result.Status);
        Assert.NotNull(result.Reason);
        Assert.Contains("crosses top-level type boundaries", result.Reason!);
        Assert.False(File.Exists(Path.Combine(ws.Root, "Bar.cs")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "Baz.cs")));
    }

    [Fact]
    public void IfDirective_ContainedWithinOneType_IsAllowed()
    {
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar\n" +
            "{\n" +
            "#if DEBUG\n" +
            "    public int DebugOnly;\n" +
            "#endif\n" +
            "}\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);

        var results = ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv"));
        var result = Assert.Single(results);
        Assert.Equal("split", result.Status);

        var barPath = Path.Combine(ws.Root, "Bar.cs");
        Assert.True(File.Exists(barPath));
        var barText = File.ReadAllText(barPath);
        Assert.Contains("#if DEBUG", barText);
        Assert.Contains("public int DebugOnly;", barText);
        Assert.Contains("#endif", barText);

        // The directive must NOT be duplicated into Foo.cs.
        Assert.DoesNotContain("DebugOnly", File.ReadAllText(fooPath));
    }
}
