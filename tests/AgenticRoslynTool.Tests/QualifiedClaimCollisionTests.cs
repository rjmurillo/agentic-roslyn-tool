using Xunit;

namespace AgenticRoslynTool.Tests;

// A qualified name is chosen after the per-file plan probed the disk, so the plan never
// checked it against the other inputs. Here it lands on a third input that stays where it
// is. Content would skip that row with "target path already exists", so the plan refuses it.
public sealed class QualifiedClaimCollisionTests
{
    private const string Shared = "\n\npublic class Shared\n{\n}\n";

    [Theory]
    [InlineData(false, "A.Shared.cs")]
    [InlineData(true, "Shared.A.cs")]
    public void QualifiedNameOnAnInputThatStays_IsRefusedInThePlan(bool typeFirst, string occupied)
    {
        using var ws = new TempWorkspace();
        var a = ws.WriteFile("A.cs", "namespace One;\n\npublic class A\n{\n}" + Shared);
        var b = ws.WriteFile("B.cs", "namespace Two;\n\npublic class B\n{\n}" + Shared);
        var stays = ws.WriteFile(occupied, "namespace Three;\n\npublic class Other\n{\n}\n");
        var taken = Path.Combine(ws.Root, occupied);

        var plan = RenameTargetCollisionTests.RunAllPhases(ws, typeFirst, [a, b, stays], out var content);

        var aRow = plan.Single(r => r.OriginalPath == a);
        Assert.Equal(("skipped", PlannedOutputClaims.Reason), (aRow.Status, aRow.Reason));
        Assert.Equal($"output {taken} is also claimed by {stays}", aRow.Note);
        Assert.Equal("split", plan.Single(r => r.OriginalPath == b).Status);
        RenameTargetCollisionTests.AssertContentKeepsThePlansPromise(plan, content);
        Assert.Equal("namespace Three;\n\npublic class Other\n{\n}\n", File.ReadAllText(stays));
    }

    [Fact]
    public void PlanWithoutCrossInputClaims_IsUnchanged()
    {
        // The pass returns the rows it was given when nothing collides, so every plan that
        // had no such collision is written exactly as before.
        var root = Path.Combine(Path.GetTempPath(), "art-claims");
        var results = new[]
        {
            FileResult.Split(Path.Combine(root, "X.cs"), Path.Combine(root, "Foo.cs"), true, null, [new NewFileResult(Path.Combine(root, "A.cs"), "A")]),
            FileResult.Split(Path.Combine(root, "Y.cs"), Path.Combine(root, "Y.cs"), false, null, [new NewFileResult(Path.Combine(root, "B.cs"), "B")]),
        };

        var resolved = SplitPlanner.ResolvePlannedOutputCollisions(results);

        Assert.Equal(results, resolved);
    }

    [Fact]
    public void NewFileOnItsOwnVacatedPath_IsNotACollision()
    {
        // Foo.cs keeps Bar and renames to Bar.cs, which frees Foo.cs for the moved Foo. A
        // row's own claims never refuse it.
        var root = Path.Combine(Path.GetTempPath(), "art-claims");
        var foo = Path.Combine(root, "Foo.cs");
        var results = new[]
        {
            FileResult.Split(foo, Path.Combine(root, "Bar.cs"), true, null, [new NewFileResult(foo, "Foo")]),
        };

        Assert.Equal(results, SplitPlanner.ResolvePlannedOutputCollisions(results));
    }
}
