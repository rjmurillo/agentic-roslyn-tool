using System;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Covers how --collision-naming is parsed. The default is file-first.
public sealed class CollisionNamingOptionTests
{
    [Fact]
    public void Parse_DefaultsToFileFirst_AndReadsBothValues()
    {
        Assert.Equal(CollisionNaming.FileFirst, Options.Parse(["--input", "list.txt"]).CollisionNaming);
        Assert.Equal(CollisionNaming.FileFirst, Options.Parse(["--input", "list.txt", "--collision-naming", "file-first"]).CollisionNaming);
        Assert.Equal(CollisionNaming.TypeFirst, Options.Parse(["--input", "list.txt", "--collision-naming", "type-first"]).CollisionNaming);
        Assert.Equal(CollisionNaming.TypeFirst, Options.Parse(["--input", "list.txt", "--collision-naming", "Type-First"]).CollisionNaming);
    }

    [Fact]
    public void Parse_RejectsAnUnknownOrMissingValue()
    {
        var unknown = Assert.Throws<ArgumentException>(() => Options.Parse(["--input", "list.txt", "--collision-naming", "typefirst"]));
        Assert.Contains("Unknown collision naming: typefirst", unknown.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Options.Parse(["--input", "list.txt", "--collision-naming"]));

        // The token after the flag is its value, so it is never read as a request for help.
        Assert.False(Options.HasMetaOption(["--collision-naming", "--help"], "--help"));
    }
}
