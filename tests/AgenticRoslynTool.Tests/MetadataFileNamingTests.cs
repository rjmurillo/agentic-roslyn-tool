using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgenticRoslynTool.Tests;

// --file-naming metadata names a generic type's file Name`N.cs, the form SA1649 expects under
// StyleCop's metadata convention. The convention shapes planned paths, so these tests pin
// that one value carries through plan, renames, and content, and that a content run under
// the other value writes nothing. The renames phase replays no manifest, so it cannot see
// a disagreement; RenamesPhase_FollowsItsOwnConventionAndReplacesThePlan pins that limit.
public sealed class MetadataFileNamingTests
{
    private const string AWithGenericFoo = "namespace N;\n\npublic class A\n{\n}\n\npublic class Foo<T>\n{\n}\n";

    [Fact]
    public void Parse_DefaultsToStyleCop()
    {
        var options = Options.Parse(["--input", "list.txt"]);

        Assert.Equal(FileNamingConvention.StyleCop, options.FileNaming);
    }

    [Theory]
    [InlineData("metadata", FileNamingConvention.Metadata)]
    [InlineData("Metadata", FileNamingConvention.Metadata)]
    [InlineData("stylecop", FileNamingConvention.StyleCop)]
    public void Parse_ReadsTheConvention(string value, object expected)
    {
        var options = Options.Parse(["--input", "list.txt", "--file-naming", value]);

        Assert.Equal((FileNamingConvention)expected, options.FileNaming);
    }

    [Fact]
    public void Parse_RejectsAnUnknownConvention()
    {
        var error = Assert.Throws<ArgumentException>(() => Options.Parse(["--input", "list.txt", "--file-naming", "pascal"]));

        Assert.Contains("Unknown file naming convention: pascal", error.Message);
    }

    [Fact]
    public void HasMetaOption_TreatsTheConventionValueAsAValue()
    {
        Assert.False(Options.HasMetaOption(["--file-naming", "--help"], "--help"));
    }

    [Theory]
    [InlineData("class Foo { }", "Foo.cs")]
    [InlineData("class Foo<T> { }", "Foo`1.cs")]
    [InlineData("class Foo<TKey, TValue> { }", "Foo`2.cs")]
    [InlineData("struct Pair<T> { }", "Pair`1.cs")]
    [InlineData("interface IBox<T> { }", "IBox`1.cs")]
    [InlineData("record Row<T>(T Value);", "Row`1.cs")]
    [InlineData("delegate void Handler<T>(T value);", "Handler`1.cs")]
    [InlineData("delegate void Handler();", "Handler.cs")]
    [InlineData("enum Kind { One }", "Kind.cs")]
    public void GetFileName_Metadata_UsesArityForGenericTypesOnly(string declaration, string expected)
    {
        var types = TopLevelType.Find(CSharpSyntaxTree.ParseText(declaration).GetCompilationUnitRoot()).ToArray();

        Assert.Equal(expected, OutputBuilder.GetFileName(Assert.Single(types), types, FileNamingConvention.Metadata));
    }

    [Fact]
    public void Metadata_NamesAMovedGenericTypeByArity()
    {
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("A.cs", AWithGenericFoo);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var result = Assert.Single(RunPlanThenContent(ws, listPath, manifestPath, FileNamingConvention.Metadata, FileNamingConvention.Metadata));

        Assert.Equal("split", result.Status);
        Assert.Equal(new[] { "A.cs", "Foo`1.cs" }, CsFiles(ws));
        Assert.Contains("class Foo<T>", File.ReadAllText(Path.Combine(ws.Root, "Foo`1.cs")));
        Assert.DoesNotContain("class Foo<T>", File.ReadAllText(source));
    }

    [Fact]
    public void Default_StillNamesALoneGenericTypeWithoutArity()
    {
        using var ws = new TempWorkspace();
        var listPath = ws.WriteInputList(ws.WriteFile("A.cs", AWithGenericFoo));

        var result = Assert.Single(ws.RunPlanThenContent(listPath, Path.Combine(ws.Root, "m.csv")));

        Assert.Equal("split", result.Status);
        Assert.Equal(new[] { "A.cs", "Foo.cs" }, CsFiles(ws));
    }

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

        var result = Assert.Single(RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), FileNamingConvention.Metadata, FileNamingConvention.Metadata));

        Assert.Equal("split", result.Status);
        Assert.False(result.GitMove);
        Assert.Equal(source, result.KeptPath);
        Assert.Equal(new[] { "Foo.cs", "Foo`1.cs", "Foo`2.cs" }, CsFiles(ws));
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

        var result = Assert.Single(RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), FileNamingConvention.Metadata, FileNamingConvention.Metadata));

        Assert.Equal("split", result.Status);
        Assert.False(result.GitMove);
        Assert.Equal(new[] { "Foo.cs", "Foo`1.cs" }, CsFiles(ws));
        Assert.Contains("class Foo\n", File.ReadAllText(source));
    }

    [Fact]
    public void Metadata_RenamesAGenericKeptFileThroughGitMvThenSplits()
    {
        // Foo.cs is the wrong name for Foo<T> under metadata, so the kept file is renamed.
        // This runs the real git mv, which is where a backtick in a path would break.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("Foo.cs", "namespace N;\n\npublic class Foo<T>\n{\n}\n\npublic class Bar\n{\n}\n");
        var before = File.ReadAllBytes(source);
        var listPath = Path.Combine(Path.GetDirectoryName(ws.Root)!, Path.GetFileName(ws.Root) + ".inputs.txt");
        var manifestPath = Path.Combine(Path.GetDirectoryName(ws.Root)!, Path.GetFileName(ws.Root) + ".manifest.csv");
        try
        {
            File.WriteAllLines(listPath, [source]);
            Git(ws.Root, "init", "--quiet");
            Git(ws.Root, "add", "Foo.cs");
            var kept = Path.Combine(ws.Root, "Foo`1.cs");

            var plan = Assert.Single(RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata));
            Assert.Equal("split", plan.Status);
            Assert.True(plan.GitMove);
            Assert.Equal(kept, plan.KeptPath);
            Assert.Equal(new[] { "Foo.cs" }, CsFiles(ws));

            var renamed = Assert.Single(RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.Metadata));
            Assert.Equal("split", renamed.Status);
            Assert.Equal(new[] { "Foo`1.cs" }, CsFiles(ws));
            Assert.Equal(before, File.ReadAllBytes(kept));

            // The index follows the move, which only git mv does. Nothing is committed in
            // this fixture, so git reports a moved staged file rather than a rename.
            Assert.Equal("Foo`1.cs", Git(ws.Root, "-c", "core.quotePath=false", "ls-files").Trim());

            var content = Assert.Single(RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata));
            Assert.Equal("split", content.Status);
            Assert.Equal(new[] { "Bar.cs", "Foo`1.cs" }, CsFiles(ws));
            Assert.Contains("class Foo<T>", File.ReadAllText(kept));
            Assert.DoesNotContain("class Bar", File.ReadAllText(kept));
        }
        finally
        {
            File.Delete(listPath);
            File.Delete(manifestPath);
        }
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

        var result = Assert.Single(RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), FileNamingConvention.Metadata, FileNamingConvention.Metadata));

        Assert.Equal("split", result.Status);
        Assert.False(result.GitMove);
        Assert.Equal(source, result.KeptPath);
        Assert.Equal($"rename collision for {taken}; kept original file name", result.Note);
        Assert.Equal(takenBefore, File.ReadAllBytes(taken));
        Assert.Equal(new[] { "Bar.cs", "Foo.cs", "Foo`1.cs" }, CsFiles(ws));
    }

    [Fact]
    public void RenamesPhase_FollowsItsOwnConventionAndReplacesThePlan()
    {
        // The renames phase recomputes the plan and its rows replace the manifest. A
        // renames run under another convention is therefore not refused: it rewrites the
        // plan, and the content check compares against the rewritten plan. This pins that
        // known limit so a change to it is deliberate. No git process starts, because
        // neither convention renames A.cs.
        using var ws = new TempWorkspace();
        var listPath = ws.WriteInputList(ws.WriteFile("A.cs", AWithGenericFoo));
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = Assert.Single(RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata));
        Assert.Equal(Path.Combine(ws.Root, "Foo`1.cs"), Assert.Single(plan.NewFiles).Path);

        var renames = Assert.Single(RunPhase(ws, listPath, manifestPath, Phase.Renames, FileNamingConvention.StyleCop));

        Assert.Equal("split", renames.Status);
        Assert.Equal(Path.Combine(ws.Root, "Foo.cs"), Assert.Single(renames.NewFiles).Path);
        Assert.Equal(Path.Combine(ws.Root, "Foo.cs"), Assert.Single(Assert.Single(ManifestWriter.Read(manifestPath)).NewFiles).Path);
    }

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

        var plan = Assert.Single(RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata));
        Assert.Equal("skipped", plan.Status);
        Assert.Equal($"target path collision within split: {Path.Combine(ws.Root, "Box`1.cs")}", plan.Reason);

        var content = Assert.Single(RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata));
        Assert.Equal("skipped", content.Status);
        Assert.Equal(text, File.ReadAllText(source));
        Assert.Equal(new[] { "Other.cs" }, CsFiles(ws));
    }

    [Theory]
    [InlineData(FileNamingConvention.Metadata, FileNamingConvention.StyleCop)]
    [InlineData(FileNamingConvention.StyleCop, FileNamingConvention.Metadata)]
    public void ContentPhase_RefusesAPlanMadeUnderTheOtherConvention(object planNaming, object contentNaming)
    {
        // Only the moved file's name differs here; the kept path and the git move flag
        // agree. The content phase substitutes manifest paths before it compares, so this
        // is the case that slips through unless computed paths are compared as well.
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("A.cs", AWithGenericFoo);
        var listPath = ws.WriteInputList(source);

        var result = Assert.Single(RunPlanThenContent(ws, listPath, Path.Combine(ws.Root, "m.csv"), (FileNamingConvention)planNaming, (FileNamingConvention)contentNaming));

        Assert.Equal("skipped", result.Status);
        Assert.Equal("content phase plan does not match manifest", result.Reason);
        Assert.Equal(AWithGenericFoo, File.ReadAllText(source));
        Assert.Equal(new[] { "A.cs" }, CsFiles(ws));
    }

    [Fact]
    public void ContentPhase_RefusesAManifestPathNoPlanCouldHoldEvenWhenItIsFree()
    {
        using var ws = new TempWorkspace();
        var source = ws.WriteFile("A.cs", AWithGenericFoo);
        var listPath = ws.WriteInputList(source);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var planRows = RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.StyleCop);
        var elsewhere = Path.Combine(ws.Root, "Elsewhere.cs");
        ManifestWriter.Write(
            planRows.Select(r => r with { NewFiles = r.NewFiles.Select(f => f with { Path = elsewhere }).ToArray() }).ToArray(),
            manifestPath);

        var result = Assert.Single(RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.StyleCop));

        Assert.Equal("skipped", result.Status);
        Assert.Equal("content phase plan does not match manifest", result.Reason);
        Assert.Equal(new[] { "A.cs" }, CsFiles(ws));
    }

    [Fact]
    public void Metadata_CrossFileCollision_QualifiesTheBacktickNameAndWritesIt()
    {
        using var ws = new TempWorkspace();
        var a = ws.WriteFile("A.cs", "namespace One;\n\npublic class A\n{\n}\n\npublic class Shared<T>\n{\n}\n");
        var b = ws.WriteFile("B.cs", "namespace Two;\n\npublic class B\n{\n}\n\npublic class Shared<T>\n{\n}\n");
        var listPath = ws.WriteInputList(a, b);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = RunPhase(ws, listPath, manifestPath, Phase.Plan, FileNamingConvention.Metadata);
        Assert.Equal(
            new[] { Path.Combine(ws.Root, "A.Shared`1.cs"), Path.Combine(ws.Root, "B.Shared`1.cs") },
            plan.SelectMany(r => r.NewFiles).Select(f => f.Path));
        Assert.Equal(
            plan.SelectMany(r => r.NewFiles).Select(f => f.Path),
            ManifestWriter.Read(manifestPath).SelectMany(r => r.NewFiles).Select(f => f.Path));

        var content = RunPhase(ws, listPath, manifestPath, Phase.Content, FileNamingConvention.Metadata);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "A.Shared`1.cs", "A.cs", "B.Shared`1.cs", "B.cs" }, CsFiles(ws));
        Assert.Contains("namespace One;", File.ReadAllText(Path.Combine(ws.Root, "A.Shared`1.cs")));
        Assert.Contains("namespace Two;", File.ReadAllText(Path.Combine(ws.Root, "B.Shared`1.cs")));
    }

    private static string[] CsFiles(TempWorkspace ws)
    {
        return Directory.GetFiles(ws.Root, "*.cs").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();
    }

    private static FileResult[] RunPlanThenContent(TempWorkspace ws, string listPath, string manifestPath, FileNamingConvention planNaming, FileNamingConvention contentNaming)
    {
        RunPhase(ws, listPath, manifestPath, Phase.Plan, planNaming);
        return RunPhase(ws, listPath, manifestPath, Phase.Content, contentNaming);
    }

    // Runs one phase and writes its manifest rows back, as Program does between processes.
    private static FileResult[] RunPhase(TempWorkspace ws, string listPath, string manifestPath, Phase phase, FileNamingConvention naming)
    {
        var options = new Options(listPath, manifestPath, ws.Root, phase, RequiredHeader: null, FileNaming: naming);
        var outcome = new FileSplitter(options).Run();
        ManifestWriter.Write(outcome.ManifestRows, manifestPath);
        return outcome.ReportRows.ToArray();
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}
