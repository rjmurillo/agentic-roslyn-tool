using Xunit;

namespace AgenticRoslynTool.Tests;

// Under metadata the file name carries the arity, so the type that stays in place is the
// one whose arity name matches the file, not merely the one that shares its simple name.
public sealed class MetadataKeptTypeTests
{
    [Fact]
    public void Metadata_KeepsTheTypeWhoseArityMatchesTheFileName()
    {
        // Foo`1.cs is already correct for Foo<T>. A simple name match would never find it,
        // keep the first type instead, and move Foo<T> out of its own file.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile(
            "Foo`1.cs",
            "namespace N;\n\npublic class Foo<T, U>\n{\n}\n\npublic class Foo\n{\n}\n\npublic class Foo<T>\n{\n}\n");
        var listPath = ws.WriteInputList(source);

        var result = Assert.Single(FileNamingRuns.RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), FileNamingConvention.Metadata, FileNamingConvention.Metadata));

        Assert.Equal("split", result.Status);
        Assert.False(result.GitMove);
        Assert.Equal(source, result.KeptPath);
        Assert.Equal(new[] { "Foo.cs", "Foo`1.cs", "Foo`2.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Contains("class Foo<T>\n", File.ReadAllText(source));
        Assert.Contains("class Foo<T, U>", File.ReadAllText(Path.Combine(ws.Root, "Foo`2.cs")));
        Assert.Contains("class Foo\n", File.ReadAllText(Path.Combine(ws.Root, "Foo.cs")));
    }

    [Fact]
    public void Metadata_KeepsTheNonGenericTypeInItsPlainNamedFile()
    {
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", "namespace N;\n\npublic class Foo<T>\n{\n}\n\npublic class Foo\n{\n}\n");
        var listPath = ws.WriteInputList(source);

        var result = Assert.Single(FileNamingRuns.RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), FileNamingConvention.Metadata, FileNamingConvention.Metadata));

        Assert.Equal("split", result.Status);
        Assert.False(result.GitMove);
        Assert.Equal(new[] { "Foo.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
        Assert.Contains("class Foo\n", File.ReadAllText(source));
    }

    [Fact]
    public void Metadata_KeepsTheOriginalNameWhenTheArityNameIsTaken()
    {
        // Foo.cs should become Foo`1.cs, but that file exists. The rename is dropped with a
        // note and the existing file is never touched.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", "namespace N;\n\npublic class Foo<T>\n{\n}\n\npublic class Bar\n{\n}\n");
        var taken = ws.WriteFile("Foo`1.cs", "namespace Elsewhere;\n\npublic class Foo<T>\n{\n}\n");
        var takenBefore = File.ReadAllBytes(taken);
        var listPath = ws.WriteInputList(source);

        var result = Assert.Single(FileNamingRuns.RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), FileNamingConvention.Metadata, FileNamingConvention.Metadata));

        Assert.Equal("split", result.Status);
        Assert.False(result.GitMove);
        Assert.Equal(source, result.KeptPath);
        Assert.Equal($"rename collision for {taken}; kept original file name", result.Note);
        Assert.Equal(takenBefore, File.ReadAllBytes(taken));
        Assert.Equal(new[] { "Bar.cs", "Foo.cs", "Foo`1.cs" }, FileNamingRuns.CsFiles(ws));
    }
}
