using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class NamespaceShapeTests
{
    [Fact]
    public void FileScopedNamespace_Splits()
    {
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n";
        RunAndAssertSplit(ws, source);
        Assert.Contains("namespace Sample.Ns;", File.ReadAllText(Path.Combine(ws.Root, "Bar.cs")));
    }

    [Fact]
    public void BlockScopedNamespace_Splits()
    {
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "\n" +
            "namespace Sample.Ns\n" +
            "{\n" +
            "    public class Foo { }\n" +
            "\n" +
            "    public class Bar { }\n" +
            "}\n";
        RunAndAssertSplit(ws, source);
        var barText = File.ReadAllText(Path.Combine(ws.Root, "Bar.cs"));
        Assert.Contains("namespace Sample.Ns", barText);
        Assert.Contains("class Bar", barText);
    }

    [Fact]
    public void NoNamespace_Splits()
    {
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n";
        RunAndAssertSplit(ws, source);
        var barText = File.ReadAllText(Path.Combine(ws.Root, "Bar.cs"));
        Assert.DoesNotContain("namespace ", barText);
        Assert.Contains("class Bar", barText);
    }

    private static void RunAndAssertSplit(TempWorkspace ws, string source)
    {
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);
        var results = ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv"));
        Assert.Equal("split", Assert.Single(results).Status);
        Assert.True(File.Exists(Path.Combine(ws.Root, "Bar.cs")));
    }
}
