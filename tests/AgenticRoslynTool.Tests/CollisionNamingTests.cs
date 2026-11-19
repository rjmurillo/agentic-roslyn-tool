using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Covers --collision-naming. The default form is pinned by FilenameCollisionTests, which
// this file leaves alone on purpose: those tests must keep passing with no flag at all.
public sealed class CollisionNamingTests
{
    private const string MismatchReason = "content phase plan does not match manifest";

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

    [Fact]
    public void TypeFirst_PutsTheTypeNameBeforeTheSourceFileName()
    {
        using var ws = new TempWorkspace();
        var (aPath, bPath, listPath) = WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = RunPhase(ws, listPath, manifestPath, Phase.Plan, CollisionNaming.TypeFirst);
        Assert.All(plan, r => Assert.Equal("split", r.Status));
        Assert.Equal(Path.Combine(ws.Root, "Shared.A.cs"), Assert.Single(plan.Single(r => r.OriginalPath == aPath).NewFiles).Path);
        Assert.Equal(Path.Combine(ws.Root, "Shared.B.cs"), Assert.Single(plan.Single(r => r.OriginalPath == bPath).NewFiles).Path);
        Assert.All(plan, r => Assert.Contains("Shared.A.cs", r.Note ?? string.Empty, StringComparison.Ordinal));

        ManifestWriter.Write(plan, manifestPath);
        var content = RunPhase(ws, listPath, manifestPath, Phase.Content, CollisionNaming.TypeFirst);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(
            new[] { "A.cs", "B.cs", "Shared.A.cs", "Shared.B.cs" },
            CsFiles(ws));
        Assert.Contains("namespace Sample.One;", File.ReadAllText(Path.Combine(ws.Root, "Shared.A.cs")), StringComparison.Ordinal);
        Assert.Contains("namespace Sample.Two;", File.ReadAllText(Path.Combine(ws.Root, "Shared.B.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("class Shared", File.ReadAllText(aPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TypeFirst_KeepsTheGenericPartOfTheFileName()
    {
        // The qualified name is built from the file name the plan already chose, so the
        // braces that tell Shared<T> from Shared stay in front of the first dot.
        using var ws = new TempWorkspace();
        var (_, _, listPath) = WriteSharedPair(ws, sharedTypes: "public class Shared { }\n\npublic class Shared<T> { }\n");
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = RunPhase(ws, listPath, manifestPath, Phase.Plan, CollisionNaming.TypeFirst);
        ManifestWriter.Write(plan, manifestPath);
        var content = RunPhase(ws, listPath, manifestPath, Phase.Content, CollisionNaming.TypeFirst);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(
            new[] { "A.cs", "B.cs", "Shared.A.cs", "Shared.B.cs", "Shared{T}.A.cs", "Shared{T}.B.cs" },
            CsFiles(ws));
        Assert.Contains("class Shared<T>", File.ReadAllText(Path.Combine(ws.Root, "Shared{T}.A.cs")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("type-first", "file-first")]
    [InlineData("file-first", "type-first")]
    public void ContentPhase_RefusesAManifestPlannedUnderTheOtherNaming(string plannedValue, string appliedValue)
    {
        // Strings, not the enum: the enum is internal and a public test method cannot take it.
        var planned = Options.Parse(["--input", "list.txt", "--collision-naming", plannedValue]).CollisionNaming;
        var applied = Options.Parse(["--input", "list.txt", "--collision-naming", appliedValue]).CollisionNaming;

        // The content phase never reruns the collision pass. Without its own check it
        // would write whatever names the manifest carries, whatever this run was told.
        using var ws = new TempWorkspace();
        var (aPath, bPath, _) = WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var cPath = ws.WriteFile("C.cs", TestHeader.Copyright + "\nnamespace Sample.Three;\n\npublic class C { }\n\npublic class Lone { }\n");
        var listPath = ws.WriteInputList(aPath, bPath, cPath);
        var aBefore = File.ReadAllBytes(aPath);
        var bBefore = File.ReadAllBytes(bPath);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        ManifestWriter.Write(RunPhase(ws, listPath, manifestPath, Phase.Plan, planned), manifestPath);
        var content = RunPhase(ws, listPath, manifestPath, Phase.Content, applied);

        var collided = content.Where(r => r.OriginalPath != cPath).ToArray();
        Assert.Equal(2, collided.Length);
        Assert.All(collided, r => Assert.Equal("skipped", r.Status));
        Assert.All(collided, r => Assert.Equal(MismatchReason, r.Reason));

        // The reason is shared with ordinary plan drift, so the note has to name the cause.
        Assert.All(collided, r => Assert.Contains($"--collision-naming {plannedValue} form and this run used {appliedValue}", r.Note ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal(aBefore, File.ReadAllBytes(aPath));
        Assert.Equal(bBefore, File.ReadAllBytes(bPath));

        // The check is per row. A row with no collision has one name under both values.
        Assert.Equal("split", content.Single(r => r.OriginalPath == cPath).Status);
        Assert.Equal(new[] { "A.cs", "B.cs", "C.cs", "Lone.cs" }, CsFiles(ws));
    }

    [Theory]
    [InlineData("SharedOne.cs")]
    [InlineData("Shared.A.cs")]
    public void ContentPhase_StillAcceptsAHandEditedFreePath(string editedName)
    {
        // Default behavior must not move. A reviewer who retypes an output path in a default
        // plan is obeyed, even when the new path has the type-first shape. The note still
        // records the name the plan chose, which is how the two cases are told apart.
        using var ws = new TempWorkspace();
        var (aPath, _, listPath) = WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = RunPhase(ws, listPath, manifestPath, Phase.Plan, CollisionNaming.FileFirst);
        var edited = plan
            .Select(r => r.OriginalPath == aPath
                ? r with { NewFiles = r.NewFiles.Select(f => f with { Path = Path.Combine(ws.Root, editedName) }).ToArray() }
                : r)
            .ToArray();
        ManifestWriter.Write(edited, manifestPath);

        var content = RunPhase(ws, listPath, manifestPath, Phase.Content, CollisionNaming.FileFirst);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "A.cs", "B.Shared.cs", "B.cs", editedName }.Order(StringComparer.Ordinal), CsFiles(ws));
    }

    [Theory]
    [InlineData("file-first")]
    [InlineData("type-first")]
    public void ContentPhase_AppliesAPlanWhoseTwoFormsAreOneName(string value)
    {
        // A.A.cs moving a type A is qualified as A.A.A.cs under both values. A run that
        // uses one value for both phases must apply it, not read it as the other form.
        var naming = Options.Parse(["--input", "list.txt", "--collision-naming", value]).CollisionNaming;
        using var ws = new TempWorkspace();
        var dotted = ws.WriteFile("A.A.cs", TestHeader.Copyright + "\nnamespace Sample.One;\n\npublic class X { }\n\npublic class A { }\n");
        var plain = ws.WriteFile("B.cs", TestHeader.Copyright + "\nnamespace Sample.Two;\n\npublic class B { }\n\npublic class A { }\n");
        var listPath = ws.WriteInputList(dotted, plain);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        ManifestWriter.Write(RunPhase(ws, listPath, manifestPath, Phase.Plan, naming), manifestPath);

        // No type is named A.A, so the plan renames the kept file to X.cs. The renames
        // phase needs git, so the move is done by hand here.
        File.Move(dotted, Path.Combine(ws.Root, "X.cs"));
        var content = RunPhase(ws, listPath, manifestPath, Phase.Content, naming);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.True(File.Exists(Path.Combine(ws.Root, "A.A.A.cs")));
    }

    [Fact]
    public void TypeFirst_RefusesWhenTheQualifiedPathAlreadyExists()
    {
        // The plan pass does not probe the disk for a qualified path, so the content phase
        // is where an occupied name is caught. It must refuse, never overwrite.
        using var ws = new TempWorkspace();
        var (aPath, _, listPath) = WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var aBefore = File.ReadAllBytes(aPath);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        ManifestWriter.Write(RunPhase(ws, listPath, manifestPath, Phase.Plan, CollisionNaming.TypeFirst), manifestPath);
        var occupied = ws.WriteFile("Shared.A.cs", "// already here\n");

        var content = RunPhase(ws, listPath, manifestPath, Phase.Content, CollisionNaming.TypeFirst);

        var a = content.Single(r => r.OriginalPath == aPath);
        Assert.Equal("skipped", a.Status);
        Assert.Equal($"target path already exists: {occupied}", a.Reason);
        Assert.Equal("// already here\n", File.ReadAllText(occupied));
        Assert.Equal(aBefore, File.ReadAllBytes(aPath));
        Assert.Equal("split", content.Single(r => r.OriginalPath != aPath).Status);
        Assert.True(File.Exists(Path.Combine(ws.Root, "Shared.B.cs")));
    }

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
            Split(Path.Combine(root, "one", "A.cs"), target),
            Split(Path.Combine(root, "two", "A.cs"), target),
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
            Split(Path.Combine(root, "one", "Foo.cs"), target),
            Split(Path.Combine(root, "two", "foo.cs"), target),
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
            Split(Path.Combine(root, sourceName), target),
            Split(Path.Combine(root, "B.cs"), target),
        };

        var typeFirst = SplitPlanner.ResolvePlannedOutputCollisions(results, CollisionNaming.TypeFirst);

        Assert.Equal("skipped", typeFirst[0].Status);
        Assert.Equal(SplitPlanner.GeneratedNameReason, typeFirst[0].Reason);
        Assert.Equal("refused output name: " + Path.Combine(root, "Shared." + sourceName), typeFirst[0].Note);
        Assert.Equal("split", typeFirst[1].Status);
        Assert.Equal(Path.Combine(root, "Shared.B.cs"), Assert.Single(typeFirst[1].NewFiles).Path);

        // The default form puts the source name first, so it is not affected and not changed.
        var fileFirst = SplitPlanner.ResolvePlannedOutputCollisions(results, CollisionNaming.FileFirst);
        Assert.All(fileFirst, r => Assert.Equal("split", r.Status));
    }

    private static (string APath, string BPath, string ListPath) WriteSharedPair(TempWorkspace ws, string sharedTypes)
    {
        var aPath = ws.WriteFile("A.cs", TestHeader.Copyright + "\nnamespace Sample.One;\n\npublic class A { }\n\n" + sharedTypes);
        var bPath = ws.WriteFile("B.cs", TestHeader.Copyright + "\nnamespace Sample.Two;\n\npublic class B { }\n\n" + sharedTypes);
        return (aPath, bPath, ws.WriteInputList(aPath, bPath));
    }

    private static IReadOnlyList<FileResult> RunPhase(TempWorkspace ws, string listPath, string manifestPath, Phase phase, CollisionNaming naming)
    {
        var options = new Options(listPath, manifestPath, ws.Root, phase, null, CollisionNaming: naming);
        return new FileSplitter(options).Run().ReportRows;
    }

    private static string[] CsFiles(TempWorkspace ws)
    {
        return Directory.GetFiles(ws.Root, "*.cs").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
    }

    private static FileResult Split(string original, string output)
    {
        return FileResult.Split(original, original, gitMove: false, note: null, new[] { new NewFileResult(output, "Shared") });
    }
}
