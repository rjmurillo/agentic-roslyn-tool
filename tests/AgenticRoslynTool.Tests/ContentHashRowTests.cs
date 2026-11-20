using Xunit;

namespace AgenticRoslynTool.Tests;

// Rows built by hand, with no file behind them: what the hash looks like in each output
// and on the rows the collision pass rewrites.
public sealed class ContentHashRowTests
{
    [Fact]
    public void QualifiedCollisionSkip_KeepsTheHash()
    {
        // Three inputs in two directories cannot produce this, so the rows are built by
        // hand: two sources whose qualified names still collide become skips.
        var hashA = ContentHash.Compute([1]);
        var hashB = ContentHash.Compute([2]);
        FileResult[] rows =
        [
            FileResult.Split("/r/A.cs", "/r/A.cs", false, null, [new NewFileResult("/r/Shared.cs", "Shared")]) with { ContentHash = hashA },
            FileResult.Split("/r/sub/../A.cs", "/r/sub/../A.cs", false, null, [new NewFileResult("/r/Shared.cs", "Shared")]) with { ContentHash = hashB },
        ];

        var resolved = SplitPlanner.ResolvePlannedOutputCollisions(rows);

        Assert.All(resolved, r => Assert.Equal("qualified output path collision requires manual handling", r.Reason));
        Assert.Equal([hashA, hashB], resolved.Select(r => r.ContentHash));
    }

    [Fact]
    public void RefusedCollisionName_KeepsTheHash()
    {
        // Under type-first naming, Shared.G.cs reads as generated code, so the plan refuses
        // that input. The refusal row still describes a file the plan read.
        var hash = ContentHash.Compute([1]);
        FileResult[] rows =
        [
            FileResult.Split("/r/G.cs", "/r/G.cs", false, null, [new NewFileResult("/r/Shared.cs", "Shared")]) with { ContentHash = hash },
            FileResult.Split("/r/B.cs", "/r/B.cs", false, null, [new NewFileResult("/r/Shared.cs", "Shared")]) with { ContentHash = ContentHash.Compute([2]) },
        ];

        var refused = SplitPlanner.ResolvePlannedOutputCollisions(rows, CollisionNaming.TypeFirst)[0];

        Assert.Equal(CollisionNames.GeneratedNameReason, refused.Reason);
        Assert.Equal(hash, refused.ContentHash);
    }

    [Fact]
    public void OversizedMalformedHash_IsQuotedShortInTheReason()
    {
        var reason = ContentHash.DescribeMismatch(new string('x', 5000), ContentHash.Compute([1, 2, 3]));

        Assert.NotNull(reason);
        Assert.True(reason.Length < 300, $"reason was {reason.Length} characters");
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

        var json = RunReport.Create(rows, Phase.Plan, "/r/manifest.csv", CollisionNaming.FileFirst).ToJson();
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var files = document.RootElement.GetProperty("files");

        Assert.Equal(hash, files[0].GetProperty("contentHash").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, files[1].GetProperty("contentHash").ValueKind);
    }
}
