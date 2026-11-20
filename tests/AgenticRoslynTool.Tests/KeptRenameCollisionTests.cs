using Xunit;

namespace AgenticRoslynTool.Tests;

// Two inputs can plan to rename their kept file to one path. The per-file plan cannot see
// that, because the target does not exist yet. The cross-file pass gives the rename to the
// first input and leaves each later one at its original name, before any git mv runs.
public sealed class KeptRenameCollisionTests
{
    private const string FooPart = "namespace N;\n\npublic partial class Foo<T>\n{\n}\n\npublic class Bar\n{\n}\n";
    private const string FooExtraPart = "namespace N;\n\npublic partial class Foo<T>\n{\n}\n\npublic class Baz\n{\n}\n";

    [Fact]
    public void Metadata_TwoPartsOfOneGenericType_RenameOnceAndBothSplit()
    {
        // Foo.cs and Foo.Extra.cs each keep a part of Foo<T>, and each plans Foo`1.cs. This
        // used to plan both renames, move the first, and fail the second.
        using var ws = new TempWorkspace();
        var first = ws.WriteFile("Foo.cs", FooPart);
        var second = ws.WriteFile("Foo.Extra.cs", FooExtraPart);
        var listPath = ws.WriteInputList(first, second);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var target = Path.Combine(ws.Root, "Foo`1.cs");
        FileNamingRuns.Git(ws.Root, "init", "--quiet");
        FileNamingRuns.Git(ws.Root, "add", "Foo.cs", "Foo.Extra.cs");

        var plan = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata);

        Assert.All(plan, r => Assert.Equal("split", r.Status));
        Assert.True(plan[0].GitMove);
        Assert.Equal(target, plan[0].KeptPath);
        Assert.False(plan[1].GitMove);
        Assert.Equal(second, plan[1].KeptPath);
        Assert.Equal($"rename collision for {target}; kept original file name", plan[1].Note);

        var renames = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.Metadata);

        Assert.All(renames, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "Foo.Extra.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Equal(FooExtraPart, File.ReadAllText(second));

        var content = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "Bar.cs", "Baz.cs", "Foo.Extra.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.DoesNotContain("class Bar", File.ReadAllText(target));
        Assert.DoesNotContain("class Baz", File.ReadAllText(second));
    }

    [Fact]
    public void Default_TwoInputsWithOneRenameTarget_PlanOneRename()
    {
        // The same gap existed without the flag: X.cs and Y.cs each keep a type named Foo
        // and each planned Foo.cs.
        using var ws = new TempWorkspace();
        var first = ws.WriteFile("X.cs", "namespace One;\n\npublic class Foo\n{\n}\n\npublic class A\n{\n}\n");
        var second = ws.WriteFile("Y.cs", "namespace Two;\n\npublic class Foo\n{\n}\n\npublic class B\n{\n}\n");
        var listPath = ws.WriteInputList(first, second);
        var target = Path.Combine(ws.Root, "Foo.cs");

        var plan = FileNamingRuns.RunPhase(ws, listPath, Path.Combine(ws.Root, "m.csv"), Phase.Plan, FileNamingConvention.StyleCop);

        Assert.All(plan, r => Assert.Equal("split", r.Status));
        Assert.Equal((target, true), (plan[0].KeptPath, plan[0].GitMove));
        Assert.Equal((second, false), (plan[1].KeptPath, plan[1].GitMove));
        Assert.Equal($"rename collision for {target}; kept original file name", plan[1].Note);
        Assert.Equal(new[] { "X.cs", "Y.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void ClaimantThePlanSkips_DoesNotTakeTheTarget()
    {
        // G.cs and H.cs each keep a type named Foo and each plan Foo.cs. Under type-first,
        // G.cs is refused, because Shared.G.cs would read as generated code. A refused row
        // must not hold the target, or H.cs would wait for a rename that never comes.
        const string Two = "\n\npublic class Foo\n{\n}\n\npublic class Shared\n{\n}\n";
        using var ws = new TempWorkspace();
        var g = ws.WriteFile("G.cs", "namespace One;" + Two);
        var h = ws.WriteFile("H.cs", "namespace Two;" + Two);
        var listPath = ws.WriteInputList(g, h);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var target = Path.Combine(ws.Root, "Foo.cs");
        FileNamingRuns.Git(ws.Root, "init", "--quiet");
        FileNamingRuns.Git(ws.Root, "add", "G.cs", "H.cs");

        var plan = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.StyleCop, CollisionNaming.TypeFirst);

        Assert.Equal("skipped", plan[0].Status);
        Assert.Equal(("split", target, true), (plan[1].Status, plan[1].KeptPath, plan[1].GitMove));

        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.StyleCop, CollisionNaming.TypeFirst);
        var content = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop, CollisionNaming.TypeFirst);

        Assert.Equal("split", content[1].Status);
        Assert.Equal(new[] { "Foo.cs", "G.cs", "Shared.H.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void WinnerTheRenamesPhaseRefuses_LeavesTheOtherInputAsPlanned()
    {
        // Foo.cs wins the target in the plan, then changes on disk. The renames phase
        // refuses it as stale. The other part would now compute the rename for itself, but
        // the plan recorded none for it and the content phase reads the planned path. So it
        // stays put, and both files wait for a new plan. Nothing is moved or written.
        using var ws = new TempWorkspace();
        var first = ws.WriteFile("Foo.cs", FooPart);
        var second = ws.WriteFile("Foo.Extra.cs", FooExtraPart);
        var listPath = ws.WriteInputList(first, second);
        var manifestPath = Path.Combine(ws.Root, "m.csv");
        var target = Path.Combine(ws.Root, "Foo`1.cs");
        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata);
        var edited = FooPart + "\n// edited after the plan\n";
        File.WriteAllText(first, edited);

        var renames = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.Metadata);

        Assert.Equal("skipped", renames[0].Status);
        Assert.StartsWith("stale plan:", renames[0].Reason, StringComparison.Ordinal);
        Assert.Equal(("split", second, false), (renames[1].Status, renames[1].KeptPath, renames[1].GitMove));
        Assert.Contains($"rename to {target} is not in the plan; kept original file name", renames[1].Note, StringComparison.Ordinal);
        Assert.Equal(new[] { "Foo.Extra.cs", "Foo.cs" }, FileNamingRuns.CsFiles(ws));

        var content = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata);

        // The winner's planned rename never landed, which the content phase reports as it
        // always has. The other part is skipped and told why.
        Assert.Equal("failed", content[0].Status);
        Assert.Contains("Run --phase renames", content[0].Reason, StringComparison.Ordinal);
        Assert.Equal("skipped", content[1].Status);
        Assert.Contains("if a planned rename has not landed, run the renames phase", content[1].Note, StringComparison.Ordinal);
        Assert.Equal(new[] { "Foo.Extra.cs", "Foo.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Equal((edited, FooExtraPart), (File.ReadAllText(first), File.ReadAllText(second)));

        // A new plan sees the edit and settles both files.
        FileNamingRuns.Git(ws.Root, "init", "--quiet");
        FileNamingRuns.Git(ws.Root, "add", "Foo.cs", "Foo.Extra.cs");
        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata);
        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.Metadata);
        var retry = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata);

        Assert.All(retry, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "Bar.cs", "Baz.cs", "Foo.Extra.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void TargetsThatDifferOnlyByCase_FollowTheDisk()
    {
        // X.cs plans Foo.cs and Y.cs plans foo.cs. Whether those are one file is the
        // disk's answer, and the content phase asks the disk. The plan has to agree, so a
        // case-sensitive platform keeps both renames and the others keep one.
        using var ws = new TempWorkspace();
        var first = ws.WriteFile("X.cs", "namespace One;\n\npublic class Foo\n{\n}\n\npublic class A\n{\n}\n");
        var second = ws.WriteFile("Y.cs", "namespace Two;\n\npublic class foo\n{\n}\n\npublic class B\n{\n}\n");
        var listPath = ws.WriteInputList(first, second);
        var oneFile = PathComparison.Comparer.Equals("Foo.cs", "foo.cs");

        var plan = FileNamingRuns.RunPhase(ws, listPath, Path.Combine(ws.Root, "m.csv"), Phase.Plan, FileNamingConvention.StyleCop);

        Assert.True(plan[0].GitMove);
        Assert.Equal(!oneFile, plan[1].GitMove);
        Assert.Equal(oneFile ? second : Path.Combine(ws.Root, "foo.cs"), plan[1].KeptPath);
    }
}
