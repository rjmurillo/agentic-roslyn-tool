using System.IO;
using System.Linq;
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
}
