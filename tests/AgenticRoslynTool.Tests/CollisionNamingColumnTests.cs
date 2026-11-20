using System;
using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The manifest's collisionNaming column and the rule that picks a run's naming from it.
// Decision 24 in docs/decision-log.md records why the column exists.
public sealed class CollisionNamingColumnTests
{
    private const string Header = "originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash,collisionNaming";

    [Fact]
    public void Write_RecordsTheValueOnEveryRow_AndReadGivesItBack()
    {
        using var ws = new TempWorkspace();
        var manifest = Path.Combine(ws.Root, "m.csv");
        var rows = new[]
        {
            FileResult.Split("/a/A.cs", "/a/A.cs", gitMove: false, note: null, [new NewFileResult("/a/X.cs", "X"), new NewFileResult("/a/Y.cs", "Y")]),
            FileResult.Skip("/a/B.cs", "/a/B.cs", "nothing to split"),
        };

        ManifestWriter.Write(rows, manifest, CollisionNaming.TypeFirst);

        var lines = File.ReadAllLines(manifest);
        Assert.Equal(Header, lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.All(lines[1..], line => Assert.EndsWith(",\"type-first\"", line, StringComparison.Ordinal));
        Assert.Equal(CollisionNaming.TypeFirst, ManifestWriter.ReadCollisionNaming(manifest));
    }

    [Fact]
    public void Read_ReturnsNullForAManifestWithNoRows()
    {
        // An empty input plans no collision, so there is no value to disagree with.
        using var ws = new TempWorkspace();
        var manifest = ws.WriteFile("m.csv", Header + "\n");

        Assert.Null(ManifestWriter.ReadCollisionNaming(manifest));
    }

    [Fact]
    public void Read_RefusesAManifestWithNoColumn()
    {
        using var ws = new TempWorkspace();
        var manifest = ws.WriteFile("m.csv", "originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash\n\"/a/A.cs\",\"/a/A.cs\",false,\"skipped\",\"\",\"\",\"\",\"\",\"\"\n");

        var error = Assert.Throws<InvalidOperationException>(() => ManifestWriter.ReadCollisionNaming(manifest));
        Assert.Contains("no collisionNaming column", error.Message, StringComparison.Ordinal);
        Assert.Contains("Re-run --phase plan", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "data row 2 has collisionNaming ''")]
    [InlineData("type-last", "data row 2 has collisionNaming 'type-last'")]
    [InlineData("file-first", "mixes collisionNaming values: data row 2 has file-first")]
    public void Read_RefusesABlankUnknownOrMixedValue(string secondRowValue, string expected)
    {
        // Each of these means someone edited the file. Guessing would pick paths nobody reviewed.
        using var ws = new TempWorkspace();
        var manifest = ws.WriteFile(
            "m.csv",
            Header + "\n"
            + "\"/a/A.cs\",\"/a/A.cs\",false,\"skipped\",\"\",\"\",\"\",\"\",\"\",\"type-first\"\n"
            + $"\"/a/B.cs\",\"/a/B.cs\",false,\"skipped\",\"\",\"\",\"\",\"\",\"\",\"{secondRowValue}\"\n");

        var error = Assert.Throws<InvalidOperationException>(() => ManifestWriter.ReadCollisionNaming(manifest));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_CountsDataRowsNotLines_WhenANoteSpansLines()
    {
        using var ws = new TempWorkspace();
        var manifest = ws.WriteFile(
            "m.csv",
            Header + "\n"
            + "\"/a/A.cs\",\"/a/A.cs\",false,\"skipped\",\"\",\"two\nlines\",\"\",\"\",\"\",\"type-first\"\n"
            + "\"/a/B.cs\",\"/a/B.cs\",false,\"skipped\",\"\",\"\",\"\",\"\",\"\",\"bogus\"\n");

        var error = Assert.Throws<InvalidOperationException>(() => ManifestWriter.ReadCollisionNaming(manifest));
        Assert.Contains("data row 2 ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, "file-first")]
    [InlineData("type-first", null, "type-first")]
    [InlineData(null, "type-first", "type-first")]
    [InlineData("type-first", "type-first", "type-first")]
    [InlineData("file-first", "file-first", "file-first")]
    public void Resolve_PrefersTheRecordedValue_ThenTheFlag_ThenFileFirst(string? given, string? recorded, string expected)
    {
        Assert.Equal(expected, CollisionNames.FlagValue(CollisionNames.Resolve(Parse(given), Parse(recorded), "m.csv")));
    }

    [Theory]
    [InlineData("file-first", "type-first")]
    [InlineData("type-first", "file-first")]
    public void Resolve_RefusesAFlagThatContradictsThePlan(string given, string recorded)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CollisionNames.Resolve(Parse(given), Parse(recorded), "m.csv"));

        Assert.Contains($"--collision-naming {given} contradicts the plan manifest, which was planned with {recorded}", error.Message, StringComparison.Ordinal);
        Assert.Contains("Omit --collision-naming", error.Message, StringComparison.Ordinal);
    }

    private static CollisionNaming? Parse(string? value) =>
        value is null ? null : CollisionNames.TryParseFlagValue(value, out var parsed) ? parsed : throw new ArgumentException(value);
}
