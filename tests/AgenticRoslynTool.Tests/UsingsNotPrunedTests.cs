using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class UsingsNotPrunedTests
{
    [Fact]
    public void UnusedLookingUsing_SurvivesIntoEveryEmittedFile()
    {
        // The tool deliberately does not prune using directives because an apparently
        // unused using can supply an extension method or a target-typed conversion.
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "using System.Linq;\n" +
            "using System.Threading.Tasks;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { public int X; }\n" +
            "\n" +
            "public class Bar { public int Y; }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);

        var results = ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv"));
        Assert.Equal("split", Assert.Single(results).Status);

        foreach (var path in new[] { fooPath, Path.Combine(ws.Root, "Bar.cs") })
        {
            var text = File.ReadAllText(path);
            Assert.Contains("using System;", text);
            Assert.Contains("using System.Linq;", text);
            Assert.Contains("using System.Threading.Tasks;", text);
        }
    }
}
