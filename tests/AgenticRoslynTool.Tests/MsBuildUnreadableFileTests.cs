using Xunit;

namespace AgenticRoslynTool.Tests;

// An MSBuild file that does not parse as XML cannot say which files it names. Every input
// in or below its directory is refused, and inputs elsewhere are not. See decision 31.
public sealed class MsBuildUnreadableFileTests : IDisposable
{
    private const string Reason = "cannot read MSBuild Compile items in src/App/App.csproj; manual split required";

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("<Project><ItemGroup><Compile Include=\"Foo.cs\"></ItemGroup></Project>")]
    [InlineData("not xml at all")]
    [InlineData("<!DOCTYPE Project [<!ENTITY e \"Foo.cs\">]><Project><Compile Include=\"&e;\" /></Project>")]
    public void UnparseableFile_RefusesInputsInAndBelowItsDirectory(string text)
    {
        // A document type declaration is refused by the reader, so it counts as unreadable
        // and no entity is ever expanded.
        _workspace.WriteFile("src/App/App.csproj", text);
        var items = MsBuildCompileItems.Scan(_workspace.Root);

        Assert.Equal(Reason, items.DescribeRefusal(PathOf("src/App/Bar.cs")));
        Assert.Equal(Reason, items.DescribeRefusal(PathOf("src/App/Models/Bar.cs")));
    }

    [Fact]
    public void UnparseableFile_LeavesSiblingAndParentDirectoriesAlone()
    {
        // The known gap, recorded in decision 31: a broken project that links a file from
        // a sibling directory does not protect that file.
        _workspace.WriteFile("src/App/App.csproj", "<Project>");
        var items = MsBuildCompileItems.Scan(_workspace.Root);

        Assert.Null(items.DescribeRefusal(PathOf("src/Bar.cs")));
        Assert.Null(items.DescribeRefusal(PathOf("src/AppTests/Bar.cs")));
    }

    [Fact]
    public void NamedRefusal_WinsOverUnreadableRefusal()
    {
        _workspace.WriteFile("src/App/App.csproj", "<Project>");
        _workspace.WriteFile("src/Lib/Lib.csproj", "<Project><ItemGroup><Compile Include=\"../App/Bar.cs\" /></ItemGroup></Project>");

        Assert.Equal(
            "file is named by an MSBuild Compile item in src/Lib/Lib.csproj; manual split required",
            MsBuildCompileItems.Scan(_workspace.Root).DescribeRefusal(PathOf("src/App/Bar.cs")));
    }

    private string PathOf(string relativePath) => Path.Combine(_workspace.Root, relativePath);
}
