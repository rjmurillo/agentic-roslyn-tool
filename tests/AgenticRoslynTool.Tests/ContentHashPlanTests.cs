using System.Security.Cryptography;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The plan phase records a hash of every input it reads, whatever the row status.
public sealed class ContentHashPlanTests : ContentHashTestBase
{
    [Fact]
    public void PlanRow_CarriesTheSha256OfTheRawInputBytes()
    {
        var source = Workspace.WriteFile("Foo.cs", TwoTypes);

        var row = Assert.Single(Plan(source));

        // Computed here independently, not through ContentHash, so the test pins the format.
        var expected = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();
        Assert.Equal("split", row.Status);
        Assert.Equal(expected, row.ContentHash);
    }

    [Fact]
    public void PlanRow_CarriesAHashForASkippedFile_AndNoneForAFileNeverRead()
    {
        var single = Workspace.WriteFile("Single.cs", "namespace N;\n\npublic class Single\n{\n}\n");
        var missing = Path.Combine(Workspace.Root, "Missing.cs");

        var rows = Plan(single, missing);

        var skipped = Assert.Single(rows, r => r.OriginalPath == single);
        Assert.Equal("skipped", skipped.Status);
        Assert.True(ContentHash.IsWellFormed(skipped.ContentHash));
        Assert.Null(Assert.Single(rows, r => r.OriginalPath == missing).ContentHash);
    }

    [Fact]
    public void FailedPlanRow_CarriesTheHashOfTheBytesItCouldNotParse()
    {
        var broken = Workspace.WriteFile("Broken.cs", "namespace N;\n\npublic class Broken {\n");

        var row = Assert.Single(Plan(broken));

        Assert.Equal("failed", row.Status);
        Assert.True(ContentHash.IsWellFormed(row.ContentHash));
    }

    [Fact]
    public void CollisionQualifiedRow_KeepsItsHash_AndIsApplied()
    {
        // Two inputs plan the same output name, so the plan rewrites both rows with a
        // qualified path. The rewritten rows must still carry the hash.
        var a = Workspace.WriteFile("A.cs", "namespace One;\n\npublic class A\n{\n}\n\npublic class Shared\n{\n}\n");
        var b = Workspace.WriteFile("B.cs", "namespace Two;\n\npublic class B\n{\n}\n\npublic class Shared\n{\n}\n");

        var planned = Plan(a, b);
        Assert.All(planned, r => Assert.Contains("resolved output path collision", r.Note, StringComparison.Ordinal));
        Assert.All(planned, r => Assert.True(ContentHash.IsWellFormed(r.ContentHash)));

        var rows = Content(a, b);

        Assert.All(rows, r => Assert.Equal("split", r.Status));
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "A.Shared.cs")));
        Assert.True(File.Exists(Path.Combine(Workspace.Root, "B.Shared.cs")));
    }
}
