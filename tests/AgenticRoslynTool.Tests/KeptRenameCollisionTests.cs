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
        // used to plan both renames, move the first, and stop the renames run at exit 3.
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
}
