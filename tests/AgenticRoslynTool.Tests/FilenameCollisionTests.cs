using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class FilenameCollisionTests
{
    [Fact]
    public void SameSimpleName_InDifferentNamespaces_ProducesQualifiedFileNames()
    {
        // Two source files each split a type named Shared. Without collision
        // resolution both would write "Shared.cs" in the same directory. The
        // splitter must detect this up front and emit "<original>.Shared.cs".
        using var ws = new TempWorkspace();

        var aSource = TestHeader.Copyright + "\n" +
            "namespace Sample.One;\n" +
            "\n" +
            "public class A { }\n" +
            "\n" +
            "public class Shared { }\n";
        var bSource = TestHeader.Copyright + "\n" +
            "namespace Sample.Two;\n" +
            "\n" +
            "public class B { }\n" +
            "\n" +
            "public class Shared { }\n";

        var aPath = ws.WriteFile("A.cs", aSource);
        var bPath = ws.WriteFile("B.cs", bSource);
        var listPath = ws.WriteInputList(aPath, bPath);

        var results = ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv"));
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("split", r.Status));

        Assert.True(File.Exists(Path.Combine(ws.Root, "A.Shared.cs")));
        Assert.True(File.Exists(Path.Combine(ws.Root, "B.Shared.cs")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "Shared.cs")));
    }
}
