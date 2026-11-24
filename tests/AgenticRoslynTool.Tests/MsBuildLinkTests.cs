using Xunit;

namespace AgenticRoslynTool.Tests;

// Symbolic links in the scanned tree. No link is followed. A link is harmless only when
// the walk reads its target by its real path; any other link refuses its directory.
public sealed class MsBuildLinkTests : IDisposable
{
    private const string Reason = "cannot read MSBuild Compile items in src/App/App.csproj; manual split required";

    private const string SharedReason = "cannot read MSBuild Compile items in src/App/Shared; manual split required";

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void SymlinkedMsBuildFile_IsNotOpened_AndRefusesItsDirectory()
    {
        // The target names nothing. Were the link followed, Bar.cs would pass.
        var target = _workspace.WriteFile("outside/Target.xml", "<Project />");
        if (!TryLink(PathOf("src/App/App.csproj"), target, directory: false))
        {
            return;
        }

        Assert.Equal(Reason, Scan().DescribeRefusal(PathOf("src/App/Bar.cs")));
    }

    [Fact]
    public void DirectoryLinkLeavingTheRoot_IsNotFollowed_AndRefusesItsParent()
    {
        // A project behind the link could name any file, and the scan does not read it.
        using var outside = new TempWorkspace();
        outside.WriteFile("Shared.projitems", "<Project><ItemGroup><Compile Include=\"Unrelated.cs\" /></ItemGroup></Project>");
        if (!TryLink(PathOf("src/App/Shared"), outside.Root, directory: true))
        {
            return;
        }

        var items = Scan();

        Assert.Equal(SharedReason, items.DescribeRefusal(PathOf("src/App/Bar.cs")));
        Assert.Null(items.DescribeRefusal(PathOf("src/Lib/Bar.cs")));
    }

    [Fact]
    public void DirectoryLinkInsideTheRoot_RefusesNothing()
    {
        // The walk reads the target by its real path, so the link adds nothing unseen.
        _workspace.WriteFile("src/Real/Real.csproj", "<Project><ItemGroup><Compile Include=\"Unrelated.cs\" /></ItemGroup></Project>");
        if (!TryLink(PathOf("src/App"), PathOf("src/Real"), directory: true))
        {
            return;
        }

        Assert.Null(Scan().DescribeRefusal(PathOf("src/Bar.cs")));
    }

    [Theory]
    [InlineData("tools/obj/Shared")]
    [InlineData("bin")]
    [InlineData(".git/shared")]
    public void DirectoryLinkIntoASkippedDirectory_RefusesItsParent(string target)
    {
        // The walk never enters bin, obj, or .git, so it never reads this target.
        _workspace.WriteFile(target + "/Shared.projitems", "<Project><ItemGroup><Compile Include=\"../../src/App/Bar.cs\" /></ItemGroup></Project>");
        if (!TryLink(PathOf("src/App/Shared"), PathOf(target), directory: true))
        {
            return;
        }

        Assert.Equal(SharedReason, Scan().DescribeRefusal(PathOf("src/App/Bar.cs")));
    }

    // Creating a link needs a privilege some Windows machines lack. The Linux CI job runs
    // these tests, as it does the other link tests in this suite.
    private static bool TryLink(string link, string target, bool directory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            _ = directory ? Directory.CreateSymbolicLink(link, target) : File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private MsBuildCompileItems Scan() => MsBuildCompileItems.Scan(_workspace.Root);

    private string PathOf(string relativePath) => Path.Combine(_workspace.Root, relativePath);
}
