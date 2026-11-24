using Xunit;

namespace AgenticRoslynTool.Tests;

// Every phase applies the MSBuild Compile item refusal, and the content phase scans again
// rather than trusting the plan. See decision 31.
public sealed class MsBuildCompilePhaseTests : ContentHashTestBase
{
    private const string LinkingProject = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Compile Include=\"..\\Foo.cs\" Link=\"Foo.cs\" />\n  </ItemGroup>\n</Project>\n";

    private const string Reason = "file is named by an MSBuild Compile item in Other/Other.csproj; manual split required";

    [Fact]
    public void Plan_RefusesALinkedFile_AndSplitsAFileNothingNames()
    {
        var foo = Workspace.WriteFile("Foo.cs", TwoTypes);
        var free = Workspace.WriteFile("Free.cs", TwoTypes.Replace("class Foo", "class Free", StringComparison.Ordinal));
        Workspace.WriteFile("Other/Other.csproj", LinkingProject);

        var rows = Plan(foo, free).ToDictionary(r => r.OriginalPath);

        Assert.Equal("skipped", rows[foo].Status);
        Assert.Equal(Reason, rows[foo].Reason);
        Assert.Equal(foo, rows[foo].KeptPath);
        Assert.False(rows[foo].GitMove);
        Assert.Equal("split", rows[free].Status);
    }

    [Fact]
    public void Content_RefusesAFileAProjectStartedNamingAfterThePlan()
    {
        var foo = Workspace.WriteFile("Foo.cs", TwoTypes);
        Assert.Equal("split", Assert.Single(Plan(foo)).Status);

        // The source is left alone, so the hash check passes and this guard is what refuses.
        Workspace.WriteFile("Other/Other.csproj", LinkingProject);
        var before = File.ReadAllBytes(foo);

        var row = Assert.Single(Content(foo));

        Assert.Equal("skipped", row.Status);
        Assert.Equal(Reason, row.Reason);
        Assert.Equal(before, File.ReadAllBytes(foo));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Bar.cs")));
    }

    [Fact]
    public void Renames_RefusesAFileAProjectStartedNamingAfterThePlan()
    {
        // A rename breaks a linked include just as a split does.
        var stuff = Workspace.WriteFile("Stuff.cs", Mismatched);
        InitRepository("Stuff.cs");
        Assert.True(Assert.Single(Plan(stuff)).GitMove);
        Workspace.WriteFile("Other/Other.csproj", LinkingProject.Replace("Foo.cs", "Stuff.cs", StringComparison.Ordinal));

        var row = Assert.Single(RunPhase(Phase.Renames, stuff));

        Assert.Equal("skipped", row.Status);
        Assert.Equal(Reason, row.Reason);
        Assert.True(File.Exists(stuff));
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Order2.cs")));
    }

    [Fact]
    public void Content_AfterARename_AsksAboutTheOriginalName()
    {
        // The content phase reads Order2.cs, but a project that names Stuff.cs lost the file
        // when the rename landed, and it would not compile Line.cs either.
        var stuff = Workspace.WriteFile("Stuff.cs", Mismatched);
        InitRepository("Stuff.cs");
        Plan(stuff);
        Assert.Equal("split", Assert.Single(RunPhase(Phase.Renames, stuff)).Status);
        Workspace.WriteFile("Other/Other.csproj", LinkingProject.Replace("Foo.cs", "Stuff.cs", StringComparison.Ordinal));

        var row = Assert.Single(Content(stuff));

        Assert.Equal("skipped", row.Status);
        Assert.Equal(Reason, row.Reason);
        Assert.False(File.Exists(Path.Combine(Workspace.Root, "Line.cs")));
    }
}
