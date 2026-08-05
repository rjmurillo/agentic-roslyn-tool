using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class SplitTypesHappyPathTests
{
    [Fact]
    public void ThreeTypes_SplitIntoThreeFiles_PrimaryStays()
    {
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n" +
            "\n" +
            "public class Baz { }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var listPath = ws.WriteInputList(fooPath);

        var manifest = Path.Combine(ws.Root, "manifest.csv");
        var results = ws.RunPlanThenContent(listPath, manifest);

        var result = Assert.Single(results);
        Assert.Equal("split", result.Status);
        Assert.False(result.GitMove, "Primary type matches file name so no rename is required.");

        var barPath = Path.Combine(ws.Root, "Bar.cs");
        var bazPath = Path.Combine(ws.Root, "Baz.cs");
        Assert.True(File.Exists(fooPath));
        Assert.True(File.Exists(barPath));
        Assert.True(File.Exists(bazPath));

        foreach (var path in new[] { fooPath, barPath, bazPath })
        {
            var text = File.ReadAllText(path);
            Assert.StartsWith(TestHeader.Copyright, text);
            Assert.Contains("using System;", text);
            Assert.Contains("using System.Collections.Generic;", text);
            Assert.Contains("namespace Sample.Ns;", text);
        }

        Assert.Contains("class Foo", File.ReadAllText(fooPath));
        Assert.DoesNotContain("class Bar", File.ReadAllText(fooPath));
        Assert.DoesNotContain("class Baz", File.ReadAllText(fooPath));

        Assert.Contains("class Bar", File.ReadAllText(barPath));
        Assert.DoesNotContain("class Foo", File.ReadAllText(barPath));
        Assert.DoesNotContain("class Baz", File.ReadAllText(barPath));

        Assert.Contains("class Baz", File.ReadAllText(bazPath));
        Assert.DoesNotContain("class Foo", File.ReadAllText(bazPath));
        Assert.DoesNotContain("class Bar", File.ReadAllText(bazPath));

        var newFileTypes = result.NewFiles.Select(f => f.Type).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "Bar", "Baz" }, newFileTypes);
    }
}
