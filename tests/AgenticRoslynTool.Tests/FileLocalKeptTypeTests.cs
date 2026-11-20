using Xunit;

namespace AgenticRoslynTool.Tests;

// Which ordinary type stays with the file-local types, and that the file keeps its name
// through every phase. The renames phase runs git, so each test tracks its files first.
public sealed class FileLocalKeptTypeTests : FileLocalTestBase
{
    private const string Helper = "file class WidgetHelper\n{\n}\n";

    private const string Gadget = "internal class Gadget\n{\n    public static string HelperName() => typeof(WidgetHelper).FullName;\n}\n\n";

    [Fact]
    public void NoTypeNamedForTheFile_TheFileIsNotRenamedInAnyPhase()
    {
        // The control has no file-local type, so the keep rule renames it. Widgets.cs has
        // the same shape plus a file-local type, and must stay Widgets.cs.
        var widgets = Write("Widgets.cs", "namespace Sample;\n\n" + Gadget + "internal class Plain\n{\n}\n\n" + Helper);
        var control = Write("Control.cs", "namespace Sample;\n\ninternal class Dial\n{\n}\n\ninternal class Knob\n{\n}\n");
        GitFixture.InitAndTrack(Workspace.Root, widgets, control);
        var before = FileLocalCompiler.MetadataName(Compile(), "WidgetHelper");

        var rows = new[] { Phase.Plan, Phase.Renames, Phase.Content }.Select(phase => Run(phase, widgets, control)).ToArray();

        Assert.All(rows, phase => Assert.Equal(("split", widgets, false), (phase[0].Status, phase[0].KeptPath, phase[0].GitMove)));
        Assert.All(rows, phase => Assert.Equal(("split", PathOf("Dial.cs"), true), (phase[1].Status, phase[1].KeptPath, phase[1].GitMove)));
        Assert.Equal(["Dial.cs", "Knob.cs", "Plain.cs", "Widgets.cs"], CsFiles());
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
        Assert.StartsWith("<Widgets>F", before, StringComparison.Ordinal);
        Assert.Equal(before, FileLocalCompiler.MetadataName(Compile(), "WidgetHelper"));
    }

    [Fact]
    public void GenericTypeInAPlainNamedFile_IsNotRenamedUnderMetadataNaming()
    {
        // Under metadata naming Foo<T> belongs in Foo`1.cs, and the control is renamed
        // that way. Foo.cs holds a file-local type, so it keeps its name.
        Naming = FileNamingConvention.Metadata;
        var foo = Write("Foo.cs", "namespace Sample;\n\ninternal class Foo<T>\n{\n    public static string HelperName() => typeof(FooHelper).FullName;\n}\n\ninternal class Bar\n{\n}\n\nfile class FooHelper\n{\n}\n");
        var control = Write("Baz.cs", "namespace Sample;\n\ninternal class Baz<T>\n{\n}\n\ninternal class Qux\n{\n}\n");
        GitFixture.InitAndTrack(Workspace.Root, foo, control);
        var before = FileLocalCompiler.MetadataName(Compile(), "FooHelper");

        var rows = new[] { Phase.Plan, Phase.Renames, Phase.Content }.Select(phase => Run(phase, foo, control)).ToArray();

        Assert.All(rows, phase => Assert.Equal(("split", foo, false), (phase[0].Status, phase[0].KeptPath, phase[0].GitMove)));
        Assert.All(rows, phase => Assert.Equal(("split", PathOf("Baz`1.cs"), true), (phase[1].Status, phase[1].KeptPath, phase[1].GitMove)));
        Assert.Equal(["Bar.cs", "Baz`1.cs", "Foo.cs", "Qux.cs"], CsFiles());
        Assert.EndsWith("the file keeps its name because a rename changes a file-local type's metadata name", rows[0][0].Note, StringComparison.Ordinal);
        Assert.Equal(before, FileLocalCompiler.MetadataName(Compile(), "FooHelper"));
    }

    [Fact]
    public void NoTypeNamedForTheFile_TheOneTypeThatNamesAFileLocalTypeIsKept()
    {
        // Plain comes first, and the keep rule alone would keep it and move Gadget. Gadget
        // names the file-local type and can never move, so Gadget is kept instead.
        var widgets = Write("Widgets.cs", "namespace Sample;\n\ninternal class Plain\n{\n}\n\n" + Gadget + Helper);

        Run(Phase.Plan, widgets);
        var row = Assert.Single(Run(Phase.Content, widgets));

        Assert.Equal(("split", widgets, false), (row.Status, row.KeptPath, row.GitMove));
        Assert.Equal("Plain", Assert.Single(row.NewFiles).Type);
        Assert.Contains("class Gadget", File.ReadAllText(widgets), StringComparison.Ordinal);
        Assert.Empty(FileLocalCompiler.Diagnostics(Compile()));
    }

    [Fact]
    public void NoTypeNamedForTheFile_AndNoTypeNamesAFileLocalType_TheFirstOrdinaryTypeIsKept()
    {
        var widgets = Write("Widgets.cs", "namespace Sample;\n\n" + Helper + "\ninternal class Plain\n{\n}\n\ninternal class Second\n{\n}\n");

        Run(Phase.Plan, widgets);
        var row = Assert.Single(Run(Phase.Content, widgets));

        Assert.Equal("Second", Assert.Single(row.NewFiles).Type);
        Assert.Contains("class Plain", File.ReadAllText(widgets), StringComparison.Ordinal);
        Assert.Contains("file class WidgetHelper", File.ReadAllText(widgets), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoTypesThatNameAFileLocalType_CannotBothStay_SoTheInputIsRefused()
    {
        var widgets = Write("Widgets.cs", "namespace Sample;\n\n" + Gadget
            + "internal class Gizmo\n{\n    public static string HelperName() => typeof(WidgetHelper).Name;\n}\n\n" + Helper);

        AssertRefused(widgets, FileLocalTypes.MovedTypeReason, "Gizmo names WidgetHelper");
    }

    [Fact]
    public void GenericTypeNamedForTheFile_IsKeptUnderMetadataNaming_EvenWhenAnotherTypeNamesTheFileLocalType()
    {
        // Under metadata naming Foo<T> does not own Foo.cs, but the keep rule still chose
        // it for its name. Gadget names the file-local type and would have to move, so the
        // input is refused. Gadget is not kept in Foo<T>'s place.
        Naming = FileNamingConvention.Metadata;
        var foo = Write("Foo.cs", "namespace Sample;\n\ninternal class Foo<T>\n{\n}\n\n" + Gadget + Helper);

        AssertRefused(foo, FileLocalTypes.MovedTypeReason, "Gadget names WidgetHelper");
    }

    [Fact]
    public void FileAlreadyNamedAsTheKeepRuleWouldNameIt_DoesNotClaimAWithheldRename()
    {
        // Foo<T> beside Foo is named Foo{T}.cs, which is this file's name. The keep rule
        // would not have renamed it, so the note must not say a rename was withheld.
        var foo = Write("Foo{T}.cs", "namespace Sample;\n\ninternal class Foo<T>\n{\n}\n\ninternal class Foo\n{\n}\n\n" + Helper);

        var row = Assert.Single(Run(Phase.Plan, foo));

        Assert.Equal(("split", "file-local type stayed in this file: WidgetHelper; SA1402 still flags the file"), (row.Status, row.Note));
    }

    [Fact]
    public void BuildPlan_CalledAlone_StillRefusesWhatTheSplitterRefusesFirst()
    {
        // FileSplitter asks FileLocalTypes before it plans. BuildPlan asks again, so a
        // second caller cannot plan around a refusal. Extension members are the case no
        // later check would catch.
        const string Source = "namespace Sample;\n\ninternal class Alpha\n{\n}\n\ninternal class Caller\n{\n    public int Run() => 3.Thrice();\n}\n\n"
            + "file static class Numbers\n{\n    public static int Thrice(this int value) => value * 3;\n}\n";
        var types = FileLocalCompiler.BuildUnchecked(PathOf("Alpha.cs"), Source).Types;

        var plan = SplitPlanner.BuildPlan(PathOf("Alpha.cs"), PathOf("Alpha.cs"), types, planned: null, directiveNote: null, CollisionNaming.FileFirst, Naming);

        Assert.Equal((FileLocalTypes.ExtensionMembersReason, "Numbers declares extension members"), (plan.SkipReason, plan.SkipNote));
        Assert.Empty(plan.Files);
    }

    [Fact]
    public void TypeNamedForTheFile_IsKept_EvenWhenAnotherTypeNamesTheFileLocalType()
    {
        // Keeping Gadget would mean moving Widgets out of Widgets.cs, which the keep rule
        // never does. So this is a refusal, not a second way to pick the kept type.
        var widgets = Write("Widgets.cs", "namespace Sample;\n\ninternal class Widgets\n{\n}\n\n" + Gadget + Helper);

        AssertRefused(widgets, FileLocalTypes.MovedTypeReason, "Gadget names WidgetHelper");
    }
}
