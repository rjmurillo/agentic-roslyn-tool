using System;
using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Covers the collision pass under --collision-naming, driven from synthetic rows.
// FilenameCollisionTests pins the default form and is left alone on purpose.
public sealed class CollisionNamingPlanTests
{
    [Fact]
    public void QualifiedNameCollision_StillSkipsBothInputsUnderTypeFirst()
    {
        // Two inputs with one base name, in different folders, both aiming at one output.
        // Either naming qualifies them to the same path, so neither may be written. This
        // pins that the existing refusal survives the new form, not the form itself.
        var root = Path.Combine(Path.GetTempPath(), "art-collision-naming");
        var target = Path.Combine(root, "Shared.cs");
        var results = new[]
        {
            CollisionNamingFixture.Split(Path.Combine(root, "one", "A.cs"), target),
            CollisionNamingFixture.Split(Path.Combine(root, "two", "A.cs"), target),
        };

        var resolved = SplitPlanner.ResolvePlannedOutputCollisions(results, CollisionNaming.TypeFirst);

        Assert.All(resolved, r => Assert.Equal("skipped", r.Status));
        Assert.All(resolved, r => Assert.Equal("qualified output path collision requires manual handling", r.Reason));
    }

    [Fact]
    public void QualifiedNameCollision_IgnoresCaseUnderTypeFirst()
    {
        // Shared.Foo.cs and Shared.foo.cs are one file on Windows and macOS. The collision
        // key ignores case everywhere, so a plan made on Linux is safe to apply anywhere.
        var root = Path.Combine(Path.GetTempPath(), "art-collision-naming");
        var target = Path.Combine(root, "Shared.cs");
        var results = new[]
        {
            CollisionNamingFixture.Split(Path.Combine(root, "one", "Foo.cs"), target),
            CollisionNamingFixture.Split(Path.Combine(root, "two", "foo.cs"), target),
        };

        var resolved = SplitPlanner.ResolvePlannedOutputCollisions(results, CollisionNaming.TypeFirst);

        Assert.All(resolved, r => Assert.Equal("skipped", r.Status));
        Assert.All(resolved, r => Assert.Equal("qualified output path collision requires manual handling", r.Reason));
    }

    [Theory]
    [InlineData("G.cs")]
    [InlineData("Designer.cs")]
    [InlineData("generated.cs")]
    [InlineData("Form.g.i.cs")]
    public void TypeFirst_RefusesANameRoslynReadsAsGeneratedCode(string sourceName)
    {
        // Shared.G.cs and Shared.Designer.cs are generated code to Roslyn. Analyzers skip
        // them and nullable annotations switch off, so writing one changes behavior.
        var root = Path.Combine(Path.GetTempPath(), "art-collision-naming");
        var target = Path.Combine(root, "Shared.cs");
        var results = new[]
        {
            CollisionNamingFixture.Split(Path.Combine(root, sourceName), target),
            CollisionNamingFixture.Split(Path.Combine(root, "B.cs"), target),
        };

        var typeFirst = SplitPlanner.ResolvePlannedOutputCollisions(results, CollisionNaming.TypeFirst);

        Assert.Equal("skipped", typeFirst[0].Status);
        Assert.Equal(CollisionNames.GeneratedNameReason, typeFirst[0].Reason);
        Assert.Equal("refused output name: " + Path.Combine(root, "Shared." + sourceName), typeFirst[0].Note);
        Assert.Equal("split", typeFirst[1].Status);
        Assert.Equal(Path.Combine(root, "Shared.B.cs"), Assert.Single(typeFirst[1].NewFiles).Path);

        // The default form puts the source name first, so it is not affected and not changed.
        var fileFirst = SplitPlanner.ResolvePlannedOutputCollisions(results, CollisionNaming.FileFirst);
        Assert.All(fileFirst, r => Assert.Equal("split", r.Status));
    }
}
