using Xunit;

namespace AgenticRoslynTool.Tests;

// The AutoMapper shape. P.cs keeps A and renames to A.cs. Q.cs keeps Q and moves A into
// A.cs. Both also move B, so B collides across files too. The plan used to mark both rows
// split. Renames created A.cs, and content then skipped Q.cs with "target path already
// exists". The plan must refuse Q.cs, so every row it marks split is one content splits.
public sealed class RenameTargetCollisionTests
{
    private const string P = "namespace One;\n\npublic class A\n{\n}\n\npublic class B\n{\n}\n";
    private const string Q = "namespace Two;\n\npublic class Q\n{\n}\n\npublic class A\n{\n}\n\npublic class B\n{\n}\n";

    // The first value picks --collision-naming type-first, the second the input order.
    public static TheoryData<bool, bool> Cases => new()
    {
        { false, false },
        { false, true },
        { true, false },
        { true, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void NewFileOnAnotherInputsRenameTarget_IsRefusedInThePlan(bool typeFirst, bool renameOwnerFirst)
    {
        using var ws = new TempWorkspace();
        var p = ws.WriteFile("P.cs", P);
        var q = ws.WriteFile("Q.cs", Q);
        var target = Path.Combine(ws.Root, "A.cs");

        var plan = RunAllPhases(ws, typeFirst, renameOwnerFirst ? [p, q] : [q, p], out var content);

        var pRow = plan.Single(r => r.OriginalPath == p);
        var qRow = plan.Single(r => r.OriginalPath == q);
        Assert.Equal(("split", target, true), (pRow.Status, pRow.KeptPath, pRow.GitMove));
        Assert.Equal(("skipped", q, false), (qRow.Status, qRow.KeptPath, qRow.GitMove));
        Assert.Equal(PlannedOutputClaims.Reason, qRow.Reason);
        Assert.Equal($"output {target} is also claimed by {p}", qRow.Note);
        Assert.NotNull(qRow.ContentHash);

        // With Q.cs refused, B no longer collides, so P.cs moves it to a plain B.cs.
        Assert.Equal(new[] { Path.Combine(ws.Root, "B.cs") }, pRow.NewFiles.Select(f => f.Path));
        Assert.Null(pRow.Note);

        AssertContentKeepsThePlansPromise(plan, content);
        Assert.Equal(new[] { "A.cs", "B.cs", "Q.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Equal(Q, File.ReadAllText(q));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TwoInputsWithOneRenameTarget_StillSplitBoth(bool typeFirst, bool firstIsX)
    {
        // Rename against rename is not a refusal. The first claimant renames, the later one
        // keeps its name, and KeptRenameCollisionTests pins that rule. This proves it holds
        // through every phase under both naming values and both input orders.
        using var ws = new TempWorkspace();
        var x = ws.WriteFile("X.cs", "namespace One;\n\npublic class Foo\n{\n}\n\npublic class A\n{\n}\n");
        var y = ws.WriteFile("Y.cs", "namespace Two;\n\npublic class Foo\n{\n}\n\npublic class B\n{\n}\n");

        var plan = RunAllPhases(ws, typeFirst, firstIsX ? [x, y] : [y, x], out var content);

        Assert.All(plan, r => Assert.Equal("split", r.Status));
        Assert.Single(plan, r => r.GitMove);
        AssertContentKeepsThePlansPromise(plan, content);
        var stayed = firstIsX ? "Y.cs" : "X.cs";
        Assert.Equal(new[] { "A.cs", "B.cs", "Foo.cs", stayed }.Order(StringComparer.Ordinal), FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void TwoInputsThatRenameOntoEachOthersNewFile_AreBothRefused()
    {
        // P.cs keeps A and moves B. Q.cs keeps B and moves A. Each rename target is the
        // other's new file. Refusing one would free the other, but which one would depend
        // on input order, so both are refused. Decision 32 records this shape.
        using var ws = new TempWorkspace();
        var p = ws.WriteFile("P.cs", P);
        var q = ws.WriteFile("Q.cs", "namespace Two;\n\npublic class B\n{\n}\n\npublic class A\n{\n}\n");

        var plan = RunAllPhases(ws, false, [p, q], out var content);

        Assert.All(plan, r => Assert.Equal(("skipped", PlannedOutputClaims.Reason), (r.Status, r.Reason)));
        AssertContentKeepsThePlansPromise(plan, content);
        Assert.Equal(new[] { "P.cs", "Q.cs" }, FileNamingRuns.CsFiles(ws));
    }

    internal static FileResult[] RunAllPhases(TempWorkspace ws, bool typeFirst, string[] inputs, out FileResult[] content)
    {
        var naming = typeFirst ? CollisionNaming.TypeFirst : CollisionNaming.FileFirst;
        var listPath = ws.WriteInputList(inputs);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        GitFixture.InitAndTrack(ws.Root, inputs);
        var plan = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.StyleCop, naming);
        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.StyleCop, naming);
        content = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop, naming);
        return plan;
    }

    // The done rule: every row the plan marks split is split by content, and content never
    // meets a target that another planned row put on disk.
    internal static void AssertContentKeepsThePlansPromise(FileResult[] plan, FileResult[] content)
    {
        Assert.DoesNotContain(content, r => r.Reason?.StartsWith("target path already exists", StringComparison.Ordinal) == true);
        foreach (var planned in plan.Where(r => r.Status == "split"))
        {
            Assert.Equal("split", content.Single(r => r.OriginalPath == planned.OriginalPath).Status);
        }
    }
}
