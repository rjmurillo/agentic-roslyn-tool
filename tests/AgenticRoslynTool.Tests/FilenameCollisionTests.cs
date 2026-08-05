using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public void TwoSourcesDifferingOnlyByCase_FollowThePlatformDefinitionOfOneFile()
    {
        // Counting distinct sources is an identity question, so it has to use the same
        // comparer the rest of the tool uses to decide two paths name one file. The
        // collision key beside it stays ignore-case on purpose, so this one pass runs
        // two different comparers and this test pins which is which.
        //
        // This assertion only bites where the two comparers differ, which is Linux, and
        // CI runs ubuntu-latest. On Windows and macOS both are ignore-case, so the test
        // still runs but proves only that the no-collision path is taken.
        var results = new[]
        {
            Split("Foo.cs", "Shared.cs"),
            Split("foo.cs", "Shared.cs"),
        };

        var resolved = FileSplitter.ResolvePlannedOutputCollisions(results);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            // One file listed twice. There is no cross-file collision to resolve, and
            // treating it as one would skip a file that splits perfectly well.
            Assert.All(resolved, r => Assert.Equal("split", r.Status));
        }
        else
        {
            // Two files both aiming at Shared.cs. The collision has to be seen: leaving
            // it undetected makes the plan advertise one target twice, and content then
            // has two rows it cannot both apply. Qualifying yields Foo.Shared.cs and
            // foo.Shared.cs, which the conservative ignore-case safety check treats as
            // colliding again, so both are handed back for manual handling.
            Assert.All(resolved, r => Assert.Equal("skipped", r.Status));
            Assert.All(resolved, r => Assert.Contains("collision", r.Reason ?? string.Empty, StringComparison.Ordinal));
        }
    }

    private static FileResult Split(string originalName, string outputName)
    {
        var root = Path.Combine(Path.GetTempPath(), "art-collision");
        var original = Path.Combine(root, originalName);
        return FileResult.Split(
            original,
            original,
            gitMove: false,
            note: null,
            new[] { new NewFileResult(Path.Combine(root, outputName), "Shared") });
    }
}
