using Xunit;

namespace AgenticRoslynTool.Tests;

// Both collision naming guards from decision 26, on a backtick name from decision 24. The
// plan is metadata and type-first, so each input plans Shared`1.A.cs or Shared`1.B.cs.
public sealed class MetadataCollisionGuardTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly string _listPath;
    private readonly string _manifestPath;
    private readonly string[] _sources;
    private readonly string[] _before;

    public MetadataCollisionGuardTests()
    {
        var (a, b, listPath, manifestPath) = MetadataCollisionTests.WriteSharedGenericPair(_ws);
        (_listPath, _manifestPath, _sources) = (listPath, manifestPath, [a, b]);
        _before = _sources.Select(File.ReadAllText).ToArray();
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void AContradictingCollisionFlag_EndsTheRunBeforeAnyWrite()
    {
        // The manifest records the value, so content told the other one stops at once.
        Run(Phase.Plan, CollisionNaming.TypeFirst);

        var error = Assert.Throws<InvalidOperationException>(() => Run(Phase.Content, CollisionNaming.FileFirst));

        Assert.Contains("--collision-naming file-first contradicts the plan manifest", error.Message, StringComparison.Ordinal);
        Assert.Equal(_before, _sources.Select(File.ReadAllText));
    }

    [Fact]
    public void AnEditedCollisionColumn_IsStillRefusedPerRow()
    {
        // The per-row refusal from decision 21 is now reached only through a column edited
        // to the other value with the paths and notes left alone.
        var planned = Run(Phase.Plan, CollisionNaming.TypeFirst, recordAs: CollisionNaming.FileFirst);
        Assert.Equal(Path.Combine(_ws.Root, "Shared`1.A.cs"), Assert.Single(planned[0].NewFiles).Path);

        var applied = Run(Phase.Content, collisionNaming: null);

        Assert.All(applied, r => Assert.Equal("content phase plan does not match manifest", r.Reason));
        Assert.All(applied, r => Assert.Contains("--collision-naming type-first form and this run used file-first", r.Note ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal(_before, _sources.Select(File.ReadAllText));
    }

    private FileResult[] Run(Phase phase, CollisionNaming? collisionNaming, CollisionNaming? recordAs = null) =>
        FileNamingRuns.RunPhase(_ws, _listPath, _manifestPath, phase, FileNamingConvention.Metadata, collisionNaming, recordAs);
}
