using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The plan manifest records a hash of each input's raw bytes. A later phase that finds
// different bytes refuses the file, so what gets applied is what was reviewed. Before the
// hash, the check was structural only: an edit that kept every type name and path passed.
public sealed class ManifestContentHashTests
{
    private const string TwoTypes = "namespace N;\n\npublic class Foo\n{\n    public int M() => 1;\n}\n\npublic class Bar\n{\n}\n";

    // Kept type does not match the file name, so the plan asks for a rename to Order2.cs.
    private const string Mismatched = "namespace N;\n\npublic class Order2 { }\n\npublic class Line { }\n";

    [Fact]
    public void PlanRow_CarriesTheSha256OfTheRawInputBytes()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var row = Assert.Single(Plan(workspace, manifestPath, source));

        // Computed here independently, not through ContentHash, so the test pins the format.
        var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        Assert.Equal("split", row.Status);
        Assert.Equal(expected, row.ContentHash);
    }

    [Fact]
    public void PlanRow_CarriesAHashForASkippedFile_AndNoneForAFileNeverRead()
    {
        using var workspace = new TempWorkspace();
        var single = workspace.WriteFile("Single.cs", "namespace N;\n\npublic class Single\n{\n}\n");
        var missing = Path.Combine(workspace.Root, "Missing.cs");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var rows = Plan(workspace, manifestPath, single, missing);

        var skipped = Assert.Single(rows, r => r.OriginalPath == single);
        Assert.Equal("skipped", skipped.Status);
        Assert.True(ContentHash.IsWellFormed(skipped.ContentHash));
        Assert.Null(Assert.Single(rows, r => r.OriginalPath == missing).ContentHash);
    }

    [Fact]
    public void UnchangedInput_IsApplied_AndTheRowKeepsThePlannedHash()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var planned = Assert.Single(Plan(workspace, manifestPath, source));

        var row = Assert.Single(Content(workspace, manifestPath, source).ReportRows);

        Assert.Equal("split", row.Status);
        Assert.Equal(planned.ContentHash, row.ContentHash);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void BodyEditAfterPlan_IsRefused_AndNothingIsWritten()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var planned = Assert.Single(Plan(workspace, manifestPath, source));

        // Same types, same paths: the structural check alone would apply this.
        File.WriteAllText(source, TwoTypes.Replace("=> 1;", "=> 2;", StringComparison.Ordinal));
        var edited = File.ReadAllBytes(source);

        var row = Assert.Single(Content(workspace, manifestPath, source).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", row.Reason, StringComparison.Ordinal);
        Assert.Contains(planned.ContentHash!, row.Reason, StringComparison.Ordinal);
        Assert.Contains("re-run --phase plan", row.Reason, StringComparison.Ordinal);
        Assert.Equal(edited, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Bar.cs")));
    }

    public static TheoryData<string, byte[]> ByteOnlyEdits()
    {
        var utf8 = new UTF8Encoding(false);
        return new TheoryData<string, byte[]>
        {
            { "trailing space", utf8.GetBytes(TwoTypes.Replace("{\n    public", "{ \n    public", StringComparison.Ordinal)) },
            { "indentation", utf8.GetBytes(TwoTypes.Replace("    public int", "\tpublic int", StringComparison.Ordinal)) },
            { "newline style", utf8.GetBytes(TwoTypes.Replace("\n", "\r\n", StringComparison.Ordinal)) },
            { "final newline", utf8.GetBytes(TwoTypes.TrimEnd('\n')) },
            { "byte order mark", [0xEF, 0xBB, 0xBF, .. utf8.GetBytes(TwoTypes)] },
            { "encoding", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(TwoTypes)] },
        };
    }

    [Theory]
    [MemberData(nameof(ByteOnlyEdits))]
    public void EditThatChangesOnlyBytes_IsRefused(string kind, byte[] editedBytes)
    {
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        Plan(workspace, manifestPath, source);

        File.WriteAllBytes(source, editedBytes);

        var row = Assert.Single(Content(workspace, manifestPath, source).ReportRows);

        Assert.True(row.Status == "skipped", $"{kind}: expected skipped, got {row.Status} ({row.Reason})");
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
        Assert.Equal(editedBytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void StaleFile_DoesNotStopTheOtherFilesInTheRun()
    {
        using var workspace = new TempWorkspace();
        var stale = workspace.WriteFile("Foo.cs", TwoTypes);
        var fresh = workspace.WriteFile("Alpha.cs", "namespace N;\n\npublic class Alpha\n{\n}\n\npublic class Beta\n{\n}\n");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        Plan(workspace, manifestPath, stale, fresh);

        File.AppendAllText(stale, "// edited\n");

        var rows = Content(workspace, manifestPath, stale, fresh).ReportRows;

        Assert.Equal("skipped", Assert.Single(rows, r => r.OriginalPath == stale).Status);
        Assert.Equal("split", Assert.Single(rows, r => r.OriginalPath == fresh).Status);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Beta.cs")));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void MovedFile_StillMatches_BecauseAMoveDoesNotChangeBytes()
    {
        using var workspace = new TempWorkspace();
        var stuff = workspace.WriteFile("Stuff.cs", Mismatched);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        Plan(workspace, manifestPath, stuff);

        // Stand in for the renames phase. The git-backed test below runs the real one.
        var keptPath = Path.Combine(workspace.Root, "Order2.cs");
        File.Move(stuff, keptPath);

        var row = Assert.Single(Content(workspace, manifestPath, stuff).ReportRows);

        Assert.Equal("split", row.Status);
        Assert.Equal(keptPath, row.KeptPath);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Line.cs")));
    }

    [Fact]
    public void MovedThenEditedFile_IsRefusedAtItsKeptPath()
    {
        using var workspace = new TempWorkspace();
        var stuff = workspace.WriteFile("Stuff.cs", Mismatched);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        Plan(workspace, manifestPath, stuff);

        var keptPath = Path.Combine(workspace.Root, "Order2.cs");
        File.Move(stuff, keptPath);
        File.AppendAllText(keptPath, "// edited after the rename\n");

        var row = Assert.Single(Content(workspace, manifestPath, stuff).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);

        // The row says where the file is, and that the plan still owes nothing but a re-plan.
        Assert.Equal(keptPath, row.KeptPath);
        Assert.True(row.GitMove);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Line.cs")));
    }

    [Fact]
    public void RealRenamesPhase_ThenContent_AppliesTheSplit()
    {
        using var workspace = new TempWorkspace();
        var stuff = workspace.WriteFile("Stuff.cs", Mismatched);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        InitRepository(workspace.Root, "Stuff.cs");
        var planned = Assert.Single(Plan(workspace, manifestPath, stuff));

        var renames = RunAndWriteManifest(workspace, manifestPath, Phase.Renames, stuff);

        var keptPath = Path.Combine(workspace.Root, "Order2.cs");
        var renamed = Assert.Single(renames.ReportRows);
        Assert.Equal("split", renamed.Status);
        Assert.Equal(planned.ContentHash, renamed.ContentHash);
        Assert.True(File.Exists(keptPath));
        Assert.False(File.Exists(stuff));

        var row = Assert.Single(Content(workspace, manifestPath, stuff).ReportRows);

        Assert.Equal("split", row.Status);
        Assert.Equal(keptPath, row.KeptPath);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Line.cs")));
    }

    [Fact]
    public void EditBetweenPlanAndRenames_IsRefusedByRenames_NotLaunderedIntoANewPlan()
    {
        // The renames phase recomputes the plan and its rows replace the manifest. Without a
        // check there, the edited bytes would be hashed into the new manifest and the
        // content phase would apply an edit nobody reviewed.
        using var workspace = new TempWorkspace();
        var stuff = workspace.WriteFile("Stuff.cs", Mismatched);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        InitRepository(workspace.Root, "Stuff.cs");
        Plan(workspace, manifestPath, stuff);

        File.WriteAllText(stuff, Mismatched.Replace("class Line { }", "class Line { int x; }", StringComparison.Ordinal));
        var edited = File.ReadAllBytes(stuff);

        var renamed = Assert.Single(RunAndWriteManifest(workspace, manifestPath, Phase.Renames, stuff).ReportRows);

        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, renamed.Reason, StringComparison.Ordinal);
        Assert.Equal(stuff, renamed.KeptPath);
        Assert.True(File.Exists(stuff));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Order2.cs")));

        var row = Assert.Single(Content(workspace, manifestPath, stuff).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.Equal(edited, File.ReadAllBytes(stuff));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Line.cs")));
    }

    [Fact]
    public void StaleRefusal_StaysRefused_WhenRenamesRunsAfterIt()
    {
        // The content phase writes its refusal back to the manifest as a skipped row. If that
        // row stored the hash it found, or if renames only looked at split rows, a renames
        // run would plan the edited file afresh and the next content run would apply it.
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var planned = Assert.Single(Plan(workspace, manifestPath, source));

        File.WriteAllText(source, TwoTypes.Replace("=> 1;", "=> 2;", StringComparison.Ordinal));
        var edited = File.ReadAllBytes(source);

        var refused = Assert.Single(RunAndWriteManifest(workspace, manifestPath, Phase.Content, source).ReportRows);
        Assert.Equal("skipped", refused.Status);
        Assert.Equal(planned.ContentHash, refused.ContentHash);

        // No rename is planned for Foo.cs, so this phase starts no git process.
        var renamed = Assert.Single(RunAndWriteManifest(workspace, manifestPath, Phase.Renames, source).ReportRows);
        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, renamed.Reason, StringComparison.Ordinal);
        Assert.Equal(planned.ContentHash, renamed.ContentHash);

        var row = Assert.Single(RunAndWriteManifest(workspace, manifestPath, Phase.Content, source).ReportRows);
        Assert.Equal("skipped", row.Status);
        Assert.Equal(edited, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Bar.cs")));

        // The plan phase is the one way out.
        Plan(workspace, manifestPath, source);
        Assert.Equal("split", Assert.Single(Content(workspace, manifestPath, source).ReportRows).Status);
    }

    [Fact]
    public void FileThePlanSkipped_ThenGainsAType_IsRefusedByRenames()
    {
        // The plan recorded this file as nothing to split. An edit that adds a type would
        // make renames plan a split nobody reviewed, so the skipped row's hash holds it too.
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", "namespace N;\n\npublic class Foo\n{\n}\n");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        Assert.Equal("skipped", Assert.Single(Plan(workspace, manifestPath, source)).Status);

        File.WriteAllText(source, TwoTypes);

        var renamed = Assert.Single(RunAndWriteManifest(workspace, manifestPath, Phase.Renames, source).ReportRows);
        Assert.Equal("skipped", renamed.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, renamed.Reason, StringComparison.Ordinal);

        var row = Assert.Single(Content(workspace, manifestPath, source).ReportRows);
        Assert.Equal("skipped", row.Status);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void CollisionQualifiedRow_KeepsItsHash_AndIsApplied()
    {
        // Two inputs plan the same output name, so the plan rewrites both rows with a
        // qualified path. The rewritten rows must still carry the hash.
        using var workspace = new TempWorkspace();
        var a = workspace.WriteFile("A.cs", "namespace One;\n\npublic class A\n{\n}\n\npublic class Shared\n{\n}\n");
        var b = workspace.WriteFile("B.cs", "namespace Two;\n\npublic class B\n{\n}\n\npublic class Shared\n{\n}\n");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");

        var planned = Plan(workspace, manifestPath, a, b);
        Assert.All(planned, r => Assert.Contains("resolved output path collision", r.Note, StringComparison.Ordinal));
        Assert.All(planned, r => Assert.True(ContentHash.IsWellFormed(r.ContentHash)));

        var rows = Content(workspace, manifestPath, a, b).ReportRows;

        Assert.All(rows, r => Assert.Equal("split", r.Status));
        Assert.True(File.Exists(Path.Combine(workspace.Root, "A.Shared.cs")));
        Assert.True(File.Exists(Path.Combine(workspace.Root, "B.Shared.cs")));
    }

    [Fact]
    public void ManifestWithoutTheHashColumn_FailsTheContentRun_BeforeAnyFileIsTouched()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var before = File.ReadAllBytes(source);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var bar = Path.Combine(workspace.Root, "Bar.cs");

        // The manifest an earlier version wrote: eight columns, no contentHash.
        File.WriteAllText(
            manifestPath,
            "originalPath,keptPath,gitMove,status,reason,note,newFilePath,type\n"
            + $"\"{source}\",\"{source}\",false,\"split\",\"\",\"\",\"{bar}\",\"Bar\"\n");

        var options = new Options(workspace.WriteInputList(source), manifestPath, workspace.Root, Phase.Content, null);
        var ex = Assert.Throws<InvalidOperationException>(() => new FileSplitter(options).Run());

        Assert.Contains("contentHash", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Re-run --phase plan", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(bar));
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
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var before = File.ReadAllBytes(source);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var planned = Plan(workspace, manifestPath, source);

        // "UPPERCASE" stands for the real hash in uppercase: right digits, wrong form.
        var tampered = hash == "UPPERCASE" ? "sha256:" + planned[0].ContentHash![7..].ToUpperInvariant() : hash;
        ManifestWriter.Write(planned.Select(r => r with { ContentHash = tampered }).ToArray(), manifestPath);

        var row = Assert.Single(Content(workspace, manifestPath, source).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
        Assert.Contains("re-run --phase plan", row.Reason, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void OversizedMalformedHash_IsQuotedShortInTheReason()
    {
        var reason = ContentHash.DescribeMismatch(new string('x', 5000), ContentHash.Compute([1, 2, 3]));

        Assert.NotNull(reason);
        Assert.True(reason.Length < 300, $"reason was {reason.Length} characters");
    }

    [Fact]
    public void Hash_SurvivesTheCsvRoundTrip_OnEveryRowShape()
    {
        using var workspace = new TempWorkspace();
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
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

        ManifestWriter.Write(rows, manifestPath);
        var read = ManifestWriter.Read(manifestPath, requireContentHash: true);

        Assert.Equal([hashA, hashB, hashC, null], read.Select(r => r.ContentHash));
        Assert.Equal(2, read[0].NewFiles.Count);

        var lines = File.ReadAllLines(manifestPath);
        Assert.Equal("originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash", lines[0]);
        Assert.Equal(6, lines.Length);
        Assert.All(lines.Skip(1).Take(2), line => Assert.EndsWith($",\"{hashA}\"", line, StringComparison.Ordinal));
    }

    [Fact]
    public void BatchedContentRuns_KeepTheHashOfRowsNotYetApplied()
    {
        // A content run rewrites the manifest and must carry every unapplied row through
        // with its hash, or the second batch would be refused for a plan nobody changed.
        using var workspace = new TempWorkspace();
        var first = workspace.WriteFile("Foo.cs", TwoTypes);
        var second = workspace.WriteFile("Alpha.cs", "namespace N;\n\npublic class Alpha\n{\n}\n\npublic class Beta\n{\n}\n");
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        Plan(workspace, manifestPath, first, second);

        var firstBatch = RunAndWriteManifest(workspace, manifestPath, Phase.Content, first);
        Assert.Equal("split", Assert.Single(firstBatch.ReportRows, r => r.OriginalPath == first).Status);

        var secondBatch = RunAndWriteManifest(workspace, manifestPath, Phase.Content, second);

        Assert.Equal("split", Assert.Single(secondBatch.ReportRows, r => r.OriginalPath == second).Status);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "Beta.cs")));
    }

    [Fact]
    public void EditedManifestOverUnchangedSource_IsStillCaughtByTheStructuralCheck()
    {
        // The hash proves the source did not change. It says nothing about the manifest, so
        // the structural comparison stays as the guard on an edited plan.
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var manifestPath = Path.Combine(workspace.Root, "manifest.csv");
        var planned = Plan(workspace, manifestPath, source);
        ManifestWriter.Write(planned.Select(r => r with { KeptPath = Path.Combine(workspace.Root, "Elsewhere.cs") }).ToArray(), manifestPath);

        var row = Assert.Single(Content(workspace, manifestPath, source).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.Equal("content phase plan does not match manifest", row.Reason);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void JsonReport_CarriesTheHash_AndNullWhereThereIsNone()
    {
        var hash = ContentHash.Compute([1]);
        FileResult[] rows =
        [
            FileResult.Split("/r/A.cs", "/r/A.cs", false, null, [new NewFileResult("/r/B.cs", "B")]) with { ContentHash = hash },
            FileResult.Skip("/r/F.cs", "/r/F.cs", "input file does not exist"),
        ];

        var json = RunReport.Create(rows, Phase.Plan, "/r/manifest.csv").ToJson();
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var files = document.RootElement.GetProperty("files");

        Assert.Equal(hash, files[0].GetProperty("contentHash").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, files[1].GetProperty("contentHash").ValueKind);
    }

    private static IReadOnlyList<FileResult> Plan(TempWorkspace workspace, string manifestPath, params string[] inputs) =>
        RunAndWriteManifest(workspace, manifestPath, Phase.Plan, inputs).ManifestRows;

    private static RunOutcome Content(TempWorkspace workspace, string manifestPath, params string[] inputs) =>
        new FileSplitter(new Options(workspace.WriteInputList(inputs), manifestPath, workspace.Root, Phase.Content, null)).Run();

    // Runs one phase and writes its manifest rows back to the manifest path, which is what
    // Program does after every phase. Tests that chain phases need that write to be real.
    private static RunOutcome RunAndWriteManifest(TempWorkspace workspace, string manifestPath, Phase phase, params string[] inputs)
    {
        var outcome = new FileSplitter(new Options(workspace.WriteInputList(inputs), manifestPath, workspace.Root, phase, null)).Run();
        ManifestWriter.Write(outcome.ManifestRows, manifestPath);
        return outcome;
    }

    // The renames phase runs git mv, which needs a repository and a tracked file. Nothing is
    // committed, so no author identity is required on the machine running the test.
    private static void InitRepository(string root, string trackedFile)
    {
        Git(root, "init", "--quiet");
        Git(root, "add", "--", trackedFile);
    }

    private static void Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start git");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }
}
