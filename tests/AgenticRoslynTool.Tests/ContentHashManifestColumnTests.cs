using Xunit;

namespace AgenticRoslynTool.Tests;

// The contentHash column itself: a manifest without it ends the run, a value that cannot
// be compared refuses the file, and the value survives the CSV round trip.
public sealed class ContentHashManifestColumnTests : ContentHashTestBase
{
    [Fact]
    public void ManifestWithoutTheHashColumn_FailsTheContentRun_BeforeAnyFileIsTouched()
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var before = File.ReadAllBytes(source);
        var bar = Path.Combine(Workspace.Root, "Bar.cs");

        // The manifest an earlier version wrote: eight columns, no contentHash.
        File.WriteAllText(
            ManifestPath,
            "originalPath,keptPath,gitMove,status,reason,note,newFilePath,type\n"
            + $"\"{source}\",\"{source}\",false,\"split\",\"\",\"\",\"{bar}\",\"Bar\"\n");

        var options = new Options(Workspace.WriteInputList(source), ManifestPath, Workspace.Root, Phase.Content, null);
        var ex = Assert.Throws<InvalidOperationException>(() => new FileSplitter(options).Run());

        Assert.Contains("contentHash", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Re-run --phase plan", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(bar));
    }

    [Theory]
    [InlineData("originalPath,keptPath,gitMove,status,reason,note,newFilePath,type\n")]
    [InlineData("this is not a manifest\n")]
    [InlineData("")]
    public void RenamesOverAManifestWithoutTheHashColumn_FailsTheRun_BeforeAnyRename(string manifestText)
    {
        var stuff = Workspace.WriteFile("Stuff.cs", Mismatched);
        InitRepository("Stuff.cs");
        File.WriteAllText(ManifestPath, manifestText);

        var options = new Options(Workspace.WriteInputList(stuff), ManifestPath, Workspace.Root, Phase.Renames, null);
        var ex = Assert.Throws<InvalidOperationException>(() => new FileSplitter(options).Run());

        Assert.Contains("Re-run --phase plan", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(stuff));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Order2.cs")));
        Assert.Equal(manifestText, File.ReadAllText(ManifestPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-hash")]
    [InlineData("sha256:")]
    [InlineData("sha256:abc123")]
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("UPPERCASE")]
    public void MissingOrMalformedHash_RefusesTheFile_AndDoesNotCrash(string? hash)
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var before = File.ReadAllBytes(source);
        var planned = Plan(source);

        // "UPPERCASE" stands for the real hash in uppercase: right digits, wrong form.
        var tampered = hash == "UPPERCASE" ? "sha256:" + planned[0].ContentHash![7..].ToUpperInvariant() : hash;
        ManifestWriter.Write(planned.Select(r => r with { ContentHash = tampered }).ToArray(), ManifestPath, CollisionNaming.FileFirst);

        var row = Assert.Single(Content(source).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(
            ContentHash.StalePlanPrefix + (hash is null ? "the manifest row carries no contentHash" : "the manifest row carries a malformed contentHash"),
            row.Reason,
            StringComparison.Ordinal);
        Assert.Contains("re-run --phase plan", row.Reason, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void Hash_SurvivesTheCsvRoundTrip_OnEveryRowShape()
    {
        var hashA = ContentHash.Compute([1]);
        var hashB = ContentHash.Compute([2]);
        var hashC = ContentHash.Compute([3]);
        FileResult[] rows =
        [
            // Two new files, so the CSV repeats this row and the hash with it.
            FileResult.Split("/r/A.cs", "/r/A.cs", false, "a note, with a comma", [new NewFileResult("/r/B.cs", "B"), new NewFileResult("/r/C.cs", "C")]) with { ContentHash = hashA },
            FileResult.Skip("/r/D.cs", "/r/D.cs", "nothing to split") with { ContentHash = hashB },
            FileResult.Failed("/r/E.cs", "/r/E.cs", "input has syntax errors") with { ContentHash = hashC },
            FileResult.Skip("/r/F.cs", "/r/F.cs", "input file does not exist"),
        ];

        ManifestWriter.Write(rows, ManifestPath, CollisionNaming.FileFirst);
        var read = ManifestWriter.Read(ManifestPath, requireContentHash: true);

        Assert.Equal([hashA, hashB, hashC, null], read.Select(r => r.ContentHash));
        Assert.Equal(2, read[0].NewFiles.Count);

        var lines = File.ReadAllLines(ManifestPath);
        // contentHash stays the ninth column. Decision 25 appended collisionNaming after it.
        Assert.Equal("originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash,collisionNaming", lines[0]);
        Assert.Equal(6, lines.Length);
        Assert.All(lines.Skip(1).Take(2), line => Assert.EndsWith($",\"{hashA}\",\"file-first\"", line, StringComparison.Ordinal));
    }
}
