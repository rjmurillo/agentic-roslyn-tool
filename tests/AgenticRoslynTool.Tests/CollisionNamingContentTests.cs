using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Covers --collision-naming through the plan and content phases on real files.
public sealed class CollisionNamingContentTests
{
    private const string MismatchReason = "content phase plan does not match manifest";

    [Fact]
    public void TypeFirst_PutsTheTypeNameBeforeTheSourceFileName()
    {
        using var ws = new TempWorkspace();
        var (aPath, bPath, listPath) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Plan, CollisionNaming.TypeFirst);
        Assert.All(plan, r => Assert.Equal("split", r.Status));
        Assert.Equal(Path.Combine(ws.Root, "Shared.A.cs"), Assert.Single(plan.Single(r => r.OriginalPath == aPath).NewFiles).Path);
        Assert.Equal(Path.Combine(ws.Root, "Shared.B.cs"), Assert.Single(plan.Single(r => r.OriginalPath == bPath).NewFiles).Path);
        Assert.All(plan, r => Assert.Contains("Shared.A.cs", r.Note ?? string.Empty, StringComparison.Ordinal));

        ManifestWriter.Write(plan, manifestPath, CollisionNaming.TypeFirst);
        var content = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Content, CollisionNaming.TypeFirst);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(
            new[] { "A.cs", "B.cs", "Shared.A.cs", "Shared.B.cs" },
            CollisionNamingFixture.CsFiles(ws));
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
        var (_, _, listPath) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n\npublic class Shared<T> { }\n");
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Plan, CollisionNaming.TypeFirst);
        ManifestWriter.Write(plan, manifestPath, CollisionNaming.TypeFirst);
        var content = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Content, CollisionNaming.TypeFirst);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(
            new[] { "A.cs", "B.cs", "Shared.A.cs", "Shared.B.cs", "Shared{T}.A.cs", "Shared{T}.B.cs" },
            CollisionNamingFixture.CsFiles(ws));
        Assert.Contains("class Shared<T>", File.ReadAllText(Path.Combine(ws.Root, "Shared{T}.A.cs")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("type-first", "file-first")]
    [InlineData("file-first", "type-first")]
    public void ContentPhase_PerRowCheckCatchesAColumnEditedToTheOtherValue(string plannedValue, string recordedValue)
    {
        // Strings, not the enum: the enum is internal and a public test method cannot take it.
        var planned = Options.Parse(["--input", "list.txt", "--collision-naming", plannedValue]).CollisionNaming!.Value;
        var recorded = Options.Parse(["--input", "list.txt", "--collision-naming", recordedValue]).CollisionNaming!.Value;

        // The column normally makes a contradiction one up-front error (see
        // CollisionNamingRecordedTests). This is the case the column cannot see: someone
        // edited the column to the other value and left the paths and notes alone. The
        // content phase never reruns the collision pass, so the per-row check is still what
        // stops it writing names the plan never chose under the value it now reads.
        using var ws = new TempWorkspace();
        var (aPath, bPath, _) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var cPath = ws.WriteFile("C.cs", TestHeader.Copyright + "\nnamespace Sample.Three;\n\npublic class C { }\n\npublic class Lone { }\n");
        var listPath = ws.WriteInputList(aPath, bPath, cPath);
        var aBefore = File.ReadAllBytes(aPath);
        var bBefore = File.ReadAllBytes(bPath);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        ManifestWriter.Write(CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Plan, planned), manifestPath, recorded);
        var content = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Content, naming: null);

        var collided = content.Where(r => r.OriginalPath != cPath).ToArray();
        Assert.Equal(2, collided.Length);
        Assert.All(collided, r => Assert.Equal("skipped", r.Status));
        Assert.All(collided, r => Assert.Equal(MismatchReason, r.Reason));

        // The reason is shared with ordinary plan drift, so the note has to name the cause.
        Assert.All(collided, r => Assert.Contains($"--collision-naming {plannedValue} form and this run used {recordedValue}", r.Note ?? string.Empty, StringComparison.Ordinal));
        Assert.Equal(aBefore, File.ReadAllBytes(aPath));
        Assert.Equal(bBefore, File.ReadAllBytes(bPath));

        // The check is per row. A row with no collision has one name under both values.
        Assert.Equal("split", content.Single(r => r.OriginalPath == cPath).Status);
        Assert.Equal(new[] { "A.cs", "B.cs", "C.cs", "Lone.cs" }, CollisionNamingFixture.CsFiles(ws));
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
        var (aPath, _, listPath) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Plan, CollisionNaming.FileFirst);
        var edited = plan
            .Select(r => r.OriginalPath == aPath
                ? r with { NewFiles = r.NewFiles.Select(f => f with { Path = Path.Combine(ws.Root, editedName) }).ToArray() }
                : r)
            .ToArray();
        ManifestWriter.Write(edited, manifestPath, CollisionNaming.FileFirst);

        var content = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Content, CollisionNaming.FileFirst);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.Equal(new[] { "A.cs", "B.Shared.cs", "B.cs", editedName }.Order(StringComparer.Ordinal), CollisionNamingFixture.CsFiles(ws));
    }

    [Theory]
    [InlineData("file-first")]
    [InlineData("type-first")]
    public void ContentPhase_AppliesAPlanWhoseTwoFormsAreOneName(string value)
    {
        // A.A.cs moving a type A is qualified as A.A.A.cs under both values. A run that
        // uses one value for both phases must apply it, not read it as the other form.
        var naming = Options.Parse(["--input", "list.txt", "--collision-naming", value]).CollisionNaming!.Value;
        using var ws = new TempWorkspace();
        var dotted = ws.WriteFile("A.A.cs", TestHeader.Copyright + "\nnamespace Sample.One;\n\npublic class X { }\n\npublic class A { }\n");
        var plain = ws.WriteFile("B.cs", TestHeader.Copyright + "\nnamespace Sample.Two;\n\npublic class B { }\n\npublic class A { }\n");
        var listPath = ws.WriteInputList(dotted, plain);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        ManifestWriter.Write(CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Plan, naming), manifestPath, naming);

        // No type is named A.A, so the plan renames the kept file to X.cs. The renames
        // phase needs git, so the move is done by hand here.
        File.Move(dotted, Path.Combine(ws.Root, "X.cs"));
        var content = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Content, naming);

        Assert.All(content, r => Assert.Equal("split", r.Status));
        Assert.True(File.Exists(Path.Combine(ws.Root, "A.A.A.cs")));
    }

    [Fact]
    public void TypeFirst_RefusesWhenTheQualifiedPathAlreadyExists()
    {
        // The plan pass does not probe the disk for a qualified path, so the content phase
        // is where an occupied name is caught. It must refuse, never overwrite.
        using var ws = new TempWorkspace();
        var (aPath, _, listPath) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var aBefore = File.ReadAllBytes(aPath);
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        CollisionNamingFixture.PlanAndRecord(ws, listPath, manifestPath, CollisionNaming.TypeFirst);
        var occupied = ws.WriteFile("Shared.A.cs", "// already here\n");

        var content = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Content, CollisionNaming.TypeFirst);

        var a = content.Single(r => r.OriginalPath == aPath);
        Assert.Equal("skipped", a.Status);
        Assert.Equal($"target path already exists: {occupied}", a.Reason);
        Assert.Equal("// already here\n", File.ReadAllText(occupied));
        Assert.Equal(aBefore, File.ReadAllBytes(aPath));
        Assert.Equal("split", content.Single(r => r.OriginalPath != aPath).Status);
        Assert.True(File.Exists(Path.Combine(ws.Root, "Shared.B.cs")));
    }

    [Theory]
    [InlineData(false, "skipped")]
    [InlineData(true, "split")]
    public void ContentPhase_ReadsThePlanNoteToTellAPlanFromAHandEdit(bool stripNote, string expectedStatus)
    {
        // Pins both edges of the note dependency that decision 21 records. A run under
        // file-first refuses a row whose path and note both carry the type-first name. With
        // the note gone the row reads as a hand edit and is applied. The column is edited to
        // file-first here, the one way a run reaches this check since decision 26.
        using var ws = new TempWorkspace();
        var (aPath, _, listPath) = CollisionNamingFixture.WriteSharedPair(ws, sharedTypes: "public class Shared { }\n");
        var manifestPath = Path.Combine(ws.Root, "m.csv");

        var plan = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Plan, CollisionNaming.TypeFirst);
        var edited = plan.Select(r => stripNote ? r with { Note = null } : r).ToArray();
        ManifestWriter.Write(edited, manifestPath, CollisionNaming.FileFirst);

        var content = CollisionNamingFixture.RunPhase(ws, listPath, manifestPath, Phase.Content, naming: null);

        Assert.All(content, r => Assert.Equal(expectedStatus, r.Status));
        Assert.Equal(stripNote, File.Exists(Path.Combine(ws.Root, "Shared.A.cs")));
        Assert.Equal(stripNote, !File.ReadAllText(aPath).Contains("class Shared", StringComparison.Ordinal));
    }
}
