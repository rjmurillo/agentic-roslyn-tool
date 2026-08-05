using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

public sealed class PlanPhaseTests
{
    [Fact]
    public void PlanPhase_WritesManifest_ButChangesNoSourceFiles()
    {
        using var ws = new TempWorkspace();
        var source = TestHeader.Copyright + "\n" +
            "namespace Sample.Ns;\n" +
            "\n" +
            "public class Foo { }\n" +
            "\n" +
            "public class Bar { }\n" +
            "\n" +
            "public class Baz { }\n";
        var fooPath = ws.WriteFile("Foo.cs", source);
        var originalBytes = File.ReadAllBytes(fooPath);
        var listPath = ws.WriteInputList(fooPath);

        var manifest = Path.Combine(ws.Root, "manifest.csv");
        var options = new Options(listPath, manifest, ws.Root, Phase.Plan, null);
        var results = new FileSplitter(options).Run().ReportRows;

        var result = Assert.Single(results);
        Assert.Equal("split", result.Status);
        Assert.Equal(2, result.NewFiles.Count);

        // Nothing on disk changed. Only the source file exists; no Bar.cs, no Baz.cs.
        Assert.True(originalBytes.SequenceEqual(File.ReadAllBytes(fooPath)));
        Assert.False(File.Exists(Path.Combine(ws.Root, "Bar.cs")));
        Assert.False(File.Exists(Path.Combine(ws.Root, "Baz.cs")));

        // Program writes the manifest, not FileSplitter, so exercise the writer explicitly.
        ManifestWriter.Write(results, manifest);
        Assert.True(File.Exists(manifest));
        var manifestText = File.ReadAllText(manifest);
        Assert.StartsWith("originalPath,keptPath,gitMove,status,reason,note,newFilePath,type", manifestText);
        Assert.Contains("split", manifestText);
        Assert.Contains("Bar", manifestText);
        Assert.Contains("Baz", manifestText);

        // Round-trip: reading the manifest back yields the same plan shape.
        var roundtrip = ManifestWriter.Read(manifest);
        var single = Assert.Single(roundtrip);
        Assert.Equal("split", single.Status);
        Assert.Equal(2, single.NewFiles.Count);
    }
}
