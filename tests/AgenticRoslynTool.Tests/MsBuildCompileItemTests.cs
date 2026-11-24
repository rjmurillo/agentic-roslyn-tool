using Xunit;

namespace AgenticRoslynTool.Tests;

// Which Compile item values refuse which file names. Each case writes one MSBuild file into
// a scratch root and asks the scan about one input. See decision 31.
public sealed class MsBuildCompileItemTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Theory]
    // AutoMapper's IntegrationTests project links a file from UnitTests.
    [InlineData("<Compile Include=\"..\\UnitTests\\SpecBase.cs\" Link=\"SpecBase.cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Include=\"$(MSBuildThisFileDirectory)SpecBase.cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Remove=\"SpecBase.cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Update=\"SpecBase.cs\" DependentUpon=\"Other.cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Include=\"**/*.cs\" Exclude=\"Legacy/SpecBase.cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Include=\"Spec*.cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Include=\"Spec?ase.cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Include=\"$(Prefix).cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Include=\"A.cs;\n   B/SpecBase.cs ;C.cs\" />", "SpecBase.cs")]
    [InlineData("<Compile Include=\"specbase.CS\" />", "SpecBase.cs")]
    [InlineData("<Compile Include=\"Spec%20Base %282%29.cs\" />", "Spec Base (2).cs")]
    [InlineData("<Compile Include=\"$([System.IO.Path]::Combine('a/b', 'c'))SpecBase.cs\" />", "SpecBase.cs")]
    public void ItemThatCanNameTheInput_IsRefused(string item, string inputName)
    {
        WriteProject("src/Other/Other.csproj", item);

        var reason = Scan().DescribeRefusal(InputPath(inputName));

        Assert.Equal("file is named by an MSBuild Compile item in src/Other/Other.csproj; manual split required", reason);
    }

    [Theory]
    [InlineData("<Compile Include=\"**/*.cs\" />")]
    [InlineData("<Compile Include=\"$(MSBuildThisFileDirectory)**\\*.cs\" />")]
    [InlineData("<Compile Remove=\"Generated/*.cs\" />")]
    [InlineData("<Compile Update=\"*\" />")]
    [InlineData("<Compile Include=\"src/**\" />")]
    [InlineData("<Compile Include=\"*.Designer.cs\" />")]
    [InlineData("<Compile Include=\"$(IntermediateOutputPath)BuildDateGenerated.cs\" />")]
    [InlineData("<Compile Include=\"OtherSpecBase.cs\" />")]
    [InlineData("<None Include=\"SpecBase.cs\" />")]
    [InlineData("<Compile Include=\"\" />")]
    public void ItemThatCannotChangeWhatCompiles_IsNotRefused(string item)
    {
        // A whole-directory wildcard also matches every new sibling, so the split compiles
        // the same types. Other items here cannot match the input name at all.
        WriteProject("src/Other/Other.csproj", item);

        Assert.Null(Scan().DescribeRefusal(InputPath("SpecBase.cs")));
    }

    [Fact]
    public void OldStyleProjectWithANamespace_IsRead()
    {
        _workspace.WriteFile(
            "Legacy/Legacy.csproj",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <ItemGroup>\n    <Compile Include=\"Models\\SpecBase.cs\" />\n  </ItemGroup>\n</Project>\n");

        Assert.Equal(
            "file is named by an MSBuild Compile item in Legacy/Legacy.csproj; manual split required",
            Scan().DescribeRefusal(InputPath("SpecBase.cs")));
    }

    [Theory]
    [InlineData("Shared/Shared.projitems")]
    [InlineData("Shared/Shared.shproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("build/Common.targets")]
    public void EveryMsBuildExtension_IsScanned(string relativePath)
    {
        WriteProject(relativePath, "<Compile Include=\"SpecBase.cs\" />");

        Assert.Contains(relativePath, Scan().DescribeRefusal(InputPath("SpecBase.cs")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bin/Release/Other.csproj")]
    [InlineData("src/obj/Other.csproj")]
    [InlineData(".git/Other.csproj")]
    [InlineData("src/Other.csproj.user")]
    public void BuildOutputGitAndOtherExtensions_AreNotScanned(string relativePath)
    {
        WriteProject(relativePath, "<Compile Include=\"SpecBase.cs\" />");

        Assert.Null(Scan().DescribeRefusal(InputPath("SpecBase.cs")));
    }

    [Fact]
    public void TwoFilesNameTheInput_TheReasonNamesTheFirstInOrdinalOrder()
    {
        WriteProject("z/Z.csproj", "<Compile Include=\"SpecBase.cs\" />");
        WriteProject("a/A.csproj", "<Compile Include=\"Spec*.cs\" />");
        WriteProject("m/M.csproj", "<Compile Include=\"SpecBase.cs\" />");

        Assert.Equal(
            "file is named by an MSBuild Compile item in a/A.csproj; manual split required",
            Scan().DescribeRefusal(InputPath("SpecBase.cs")));
    }

    [Fact]
    public void EitherPathOfTheInput_IsAsked()
    {
        // The content phase reads the kept path once the renames phase has moved the file.
        WriteProject("App.csproj", "<Compile Include=\"Stuff.cs\" />");

        Assert.NotNull(Scan().DescribeRefusal(InputPath("Order.cs"), InputPath("Stuff.cs")));
    }

    [Fact]
    public void MissingRepositoryRoot_EndsTheRun()
    {
        var missing = Path.Combine(_workspace.Root, "missing");

        var error = Assert.Throws<InvalidOperationException>(() => MsBuildCompileItems.Scan(missing));

        Assert.StartsWith($"cannot scan {missing} for MSBuild files: ", error.Message, StringComparison.Ordinal);
    }

    private MsBuildCompileItems Scan() => MsBuildCompileItems.Scan(_workspace.Root);

    private string InputPath(string name) => Path.Combine(_workspace.Root, "src", "UnitTests", name);

    private void WriteProject(string relativePath, string item) =>
        _workspace.WriteFile(relativePath, $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    {item}\n  </ItemGroup>\n</Project>\n");
}
