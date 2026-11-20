using Xunit;

namespace AgenticRoslynTool.Tests;

// Two types can map to one metadata file name. Inside one input that is refused. Across
// two inputs it is resolved by the qualified form, which also carries a backtick.
public sealed class MetadataCollisionTests
{
    [Fact]
    public void Metadata_SameNameAndArityInOneFile_IsRefusedAsCollision()
    {
        // Box<T> and Box<U> both map to Box`1.cs. The stylecop convention tells them apart
        // by parameter name; metadata cannot, so the split must refuse and never overwrite.
        using var ws = new TempWorkspace();
        var text = "namespace N;\n\npublic class Other\n{\n}\n\npublic class Box<T>\n{\n}\n\npublic class Box<U>\n{\n}\n";
        var source = ws.WriteFile("Other.cs", text);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata));
        Assert.Equal("skipped", plan.Status);
        Assert.Equal($"target path collision within split: {Path.Combine(ws.Root, "Box`1.cs")}", plan.Reason);

        var content = Assert.Single(FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata));
        Assert.Equal("skipped", content.Status);
        Assert.Equal(text, File.ReadAllText(source));
        Assert.Equal(new[] { "Other.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void Metadata_CrossFileCollision_QualifiesTheBacktickNameAndWritesIt()
    {
        using var ws = new TempWorkspace();
        var a = ws.WriteFile("A.cs", "namespace One;\n\npublic class A\n{\n}\n\npublic class Shared<T>\n{\n}\n");
        var b = ws.WriteFile("B.cs", "namespace Two;\n\npublic class B\n{\n}\n\npublic class Shared<T>\n{\n}\n");
        var listPath = ws.WriteInputList(a, b);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata);
        Assert.Equal(
            new[] { Path.Combine(ws.Root, "A.Shared`1.cs"), Path.Combine(ws.Root, "B.Shared`1.cs") },
            plan.SelectMany(r => r.NewFiles).Select(f => f.Path));
        Assert.Equal(
            plan.SelectMany(r => r.NewFiles).Select(f => f.Path),
            ManifestWriter.Read(manifestPath).SelectMany(r => r.NewFiles).Select(f => f.Path));

        var content = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "A.Shared`1.cs", "A.cs", "B.Shared`1.cs", "B.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Contains("namespace One;", File.ReadAllText(Path.Combine(ws.Root, "A.Shared`1.cs")));
        Assert.Contains("namespace Two;", File.ReadAllText(Path.Combine(ws.Root, "B.Shared`1.cs")));
    }

    [Fact]
    public void MetadataWithTypeFirst_CrossFileCollision_PutsTheArityNameFirstAndWritesIt()
    {
        // The two flags compose. Type-first puts the type's file name before the source
        // file name, and under metadata that name carries the arity. SA1649 reads the name
        // up to the first dot, which is Shared`1, so the qualified file passes.
        using var ws = new TempWorkspace();
        var (a, b, listPath, manifestPath) = WriteSharedGenericPair(ws);
        var aBefore = File.ReadAllText(a);

        var plan = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata, CollisionNaming.TypeFirst);
        Assert.All(plan, r => Assert.Equal("split", r.Status));
        Assert.Equal(
            new[] { Path.Combine(ws.Root, "Shared`1.A.cs"), Path.Combine(ws.Root, "Shared`1.B.cs") },
            plan.SelectMany(r => r.NewFiles).Select(f => f.Path));
        Assert.Equal(aBefore, File.ReadAllText(a));
        Assert.Equal(new[] { "A.cs", "B.cs" }, FileNamingRuns.CsFiles(ws));

        var content = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata, CollisionNaming.TypeFirst);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "A.cs", "B.cs", "Shared`1.A.cs", "Shared`1.B.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Contains("namespace One;", File.ReadAllText(Path.Combine(ws.Root, "Shared`1.A.cs")));
        Assert.Contains("namespace Two;", File.ReadAllText(Path.Combine(ws.Root, "Shared`1.B.cs")));
        Assert.DoesNotContain("Shared<T>", File.ReadAllText(a));
        Assert.DoesNotContain("Shared<T>", File.ReadAllText(b));
    }

    [Fact]
    public void MetadataWithTypeFirst_ContentWithoutFileNaming_IsRefusedAndWritesNothing()
    {
        // The qualified name still carries the other convention's arity name, so dropping
        // --file-naming in the content run is caught even though the collision flag agrees.
        using var ws = new TempWorkspace();
        var (a, b, listPath, manifestPath) = WriteSharedGenericPair(ws);
        var aBefore = File.ReadAllText(a);
        var bBefore = File.ReadAllText(b);
        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata, CollisionNaming.TypeFirst);

        var content = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop, CollisionNaming.TypeFirst);

        Assert.All(content, r => Assert.Equal("skipped", r.Status));
        Assert.All(content, r => Assert.Equal("content phase plan does not match manifest", r.Reason));
        Assert.All(content, r => Assert.Contains("is the --file-naming metadata name and this run used stylecop", r.Note ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal(aBefore, File.ReadAllText(a));
        Assert.Equal(bBefore, File.ReadAllText(b));
        Assert.Equal(new[] { "A.cs", "B.cs" }, FileNamingRuns.CsFiles(ws));
    }

    [Fact]
    public void MetadataWithTypeFirst_ContentWithFileFirst_IsRefusedByTheCollisionCheck()
    {
        // The other direction: the convention agrees and the collision flag does not. The
        // existing collision naming refusal still fires on a backtick name.
        using var ws = new TempWorkspace();
        var (a, b, listPath, manifestPath) = WriteSharedGenericPair(ws);
        var aBefore = File.ReadAllText(a);
        var bBefore = File.ReadAllText(b);
        FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata, CollisionNaming.TypeFirst);

        var content = FileNamingRuns.RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata, CollisionNaming.FileFirst);

        Assert.All(content, r => Assert.Equal("skipped", r.Status));
        Assert.All(content, r => Assert.Contains("--collision-naming type-first form and this run used file-first", r.Note ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal(aBefore, File.ReadAllText(a));
        Assert.Equal(bBefore, File.ReadAllText(b));
        Assert.Equal(new[] { "A.cs", "B.cs" }, FileNamingRuns.CsFiles(ws));
    }

    private static (string A, string B, string ListPath, string ManifestPath) WriteSharedGenericPair(TempWorkspace ws)
    {
        var a = ws.WriteFile("A.cs", "namespace One;\n\npublic class A\n{\n}\n\npublic class Shared<T>\n{\n}\n");
        var b = ws.WriteFile("B.cs", "namespace Two;\n\npublic class B\n{\n}\n\npublic class Shared<T>\n{\n}\n");
        return (a, b, ws.WriteInputList(a, b), Path.Combine(ws.Root, "m.csv"));
    }
}
