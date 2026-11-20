using Xunit;

namespace AgenticRoslynTool.Tests;

// How a renames or content run picks its collision naming from the flag and the manifest.
// Decision 25.
public sealed class CollisionNamingFlagTests
{
    [Theory]
    [InlineData(null, null, "file-first")]
    [InlineData("type-first", null, "type-first")]
    [InlineData(null, "type-first", "type-first")]
    [InlineData("type-first", "type-first", "type-first")]
    [InlineData("file-first", "file-first", "file-first")]
    public void Resolve_PrefersTheRecordedValue_ThenTheFlag_ThenFileFirst(string? given, string? recorded, string expected)
    {
        Assert.Equal(expected, CollisionNamingFlag.Format(CollisionNamingFlag.Resolve(Parse(given), Parse(recorded), "m.csv")));
    }

    [Theory]
    [InlineData("file-first", "type-first")]
    [InlineData("type-first", "file-first")]
    public void Resolve_RefusesAFlagThatContradictsThePlan(string given, string recorded)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CollisionNamingFlag.Resolve(Parse(given), Parse(recorded), "m.csv"));

        Assert.Contains($"--collision-naming {given} contradicts the plan manifest, which was planned with {recorded}", error.Message, StringComparison.Ordinal);
        Assert.Contains("Omit the flag to use the plan's value", error.Message, StringComparison.Ordinal);
    }

    private static CollisionNaming? Parse(string? value) =>
        value is null ? null : CollisionNamingFlag.TryParse(value, out var parsed) ? parsed : throw new ArgumentException(value);
}
