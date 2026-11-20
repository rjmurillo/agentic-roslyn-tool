using System.Text;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The content phase refuses a file whose bytes differ from the plan. Before the hash, the
// check was structural only: an edit that kept every type name and path was applied.
public sealed class ContentHashContentPhaseTests : ContentHashTestBase
{
    [Fact]
    public void UnchangedInput_IsApplied_AndTheRowKeepsThePlannedHash()
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));

        var row = Assert.Single(Content(source).ReportRows);

        Assert.Equal("split", row.Status);
        Assert.Equal(planned.ContentHash, row.ContentHash);
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void BodyEditAfterPlan_IsRefused_AndNothingIsWritten()
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        var planned = Assert.Single(Plan(source));

        // Same types, same paths: the structural check alone would apply this.
        File.WriteAllText(source, TwoTypes.Replace("=> 1;", "=> 2;", StringComparison.Ordinal));
        var edited = File.ReadAllBytes(source);

        var row = Assert.Single(Content(source).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith("stale plan: input changed after the plan was written", row.Reason, StringComparison.Ordinal);
        Assert.Contains(planned.ContentHash!, row.Reason, StringComparison.Ordinal);
        Assert.Contains("re-run --phase plan", row.Reason, StringComparison.Ordinal);
        Assert.Equal(edited, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
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
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);
        Plan(source);

        File.WriteAllBytes(source, editedBytes);

        var row = Assert.Single(Content(source).ReportRows);

        Assert.True(row.Status == "skipped", $"{kind}: expected skipped, got {row.Status} ({row.Reason})");
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);
        Assert.Equal(editedBytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void StaleFile_DoesNotStopTheOtherFilesInTheRun()
    {
        var stale = Workspace.WriteFile("Foo.cs", TwoTypes);
        var fresh = Workspace.WriteFile("Alpha.cs", "namespace N;\n\npublic class Alpha\n{\n}\n\npublic class Beta\n{\n}\n");
        Plan(stale, fresh);

        File.AppendAllText(stale, "// edited\n");

        var rows = Content(stale, fresh).ReportRows;

        Assert.Equal("skipped", Assert.Single(rows, r => r.OriginalPath == stale).Status);
        Assert.Equal("split", Assert.Single(rows, r => r.OriginalPath == fresh).Status);
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "Beta.cs")));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void MovedFile_StillMatches_BecauseAMoveDoesNotChangeBytes()
    {
        var stuff = Workspace.WriteFile("Stuff.cs", Mismatched);
        Plan(stuff);

        // Stand in for the renames phase. ContentHashRenamesTests runs the real one.
        var keptPath = Path.Combine(Workspace.Root, "Order2.cs");
        File.Move(stuff, keptPath);

        var row = Assert.Single(Content(stuff).ReportRows);

        Assert.Equal("split", row.Status);
        Assert.Equal(keptPath, row.KeptPath);
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "Line.cs")));
    }

    [Fact]
    public void MovedThenEditedFile_IsRefusedAtItsKeptPath()
    {
        var stuff = Workspace.WriteFile("Stuff.cs", Mismatched);
        Plan(stuff);

        var keptPath = Path.Combine(Workspace.Root, "Order2.cs");
        File.Move(stuff, keptPath);
        File.AppendAllText(keptPath, "// edited after the rename\n");

        var row = Assert.Single(Content(stuff).ReportRows);

        Assert.Equal("skipped", row.Status);
        Assert.StartsWith(ContentHash.StalePlanPrefix, row.Reason, StringComparison.Ordinal);

        // The row says where the file is, and that the plan still owes nothing but a re-plan.
        Assert.Equal(keptPath, row.KeptPath);
        Assert.True(row.GitMove);
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Line.cs")));
    }
}
