using System.Diagnostics;
using Xunit;

namespace AgenticRoslynTool.Tests;

// MSBuild files are repository content, so the scan treats them as untrusted. A file it
// will not open refuses the inputs it could cover, and no pattern can stall the run.
public sealed class MsBuildScanSafetyTests : IDisposable
{
    private const string Reason = "cannot read MSBuild Compile items in src/App/App.csproj; manual split required";

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void SymlinkedMsBuildFile_IsNotOpened_AndRefusesItsDirectory()
    {
        // The target names nothing. Were the link followed, Bar.cs would pass.
        var target = _workspace.WriteFile("outside/Target.xml", "<Project />");
        Directory.CreateDirectory(PathOf("src/App"));
        File.CreateSymbolicLink(PathOf("src/App/App.csproj"), target);

        Assert.Equal(Reason, Scan().DescribeRefusal(PathOf("src/App/Bar.cs")));
    }

    [Fact]
    public void EmptyMsBuildFile_RefusesItsDirectory()
    {
        _workspace.WriteFile("src/App/App.csproj", string.Empty);

        Assert.Equal(Reason, Scan().DescribeRefusal(PathOf("src/App/Bar.cs")));
    }

    [Fact]
    public void OversizedMsBuildFile_IsNotRead_AndRefusesItsDirectory()
    {
        // Valid XML that names nothing, so only the size cap can refuse Bar.cs.
        Directory.CreateDirectory(PathOf("src/App"));
        using (var writer = new StreamWriter(PathOf("src/App/App.csproj")))
        {
            writer.Write("<Project>");
            writer.Write(new string(' ', (int)MsBuildFileFinder.MaxFileBytes));
            writer.Write("</Project>");
        }

        Assert.Equal(Reason, Scan().DescribeRefusal(PathOf("src/App/Bar.cs")));
    }

    [Fact]
    public async Task NamedPipeWithAnMsBuildName_DoesNotBlockTheScan()
    {
        // Opening a FIFO for reading waits for a writer that never comes.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(PathOf("src/App"));
        using (var mkfifo = Process.Start("mkfifo", PathOf("src/App/App.csproj")))
        {
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        // WaitAsync throws TimeoutException when the scan blocks, which fails the test.
        var items = await Task.Run(Scan).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Reason, items.DescribeRefusal(PathOf("src/App/Bar.cs")));
    }

    [Fact]
    public async Task PatternWithManyWildcards_DoesNotStall()
    {
        // A backtracking engine takes time exponential in the wildcard count on a name
        // that almost matches. This one would not finish within the test run.
        var wildcards = string.Concat(Enumerable.Repeat("*a", 40)) + "*b.cs";
        _workspace.WriteFile("src/App/App.csproj", $"<Project><ItemGroup><Compile Include=\"{wildcards}\" /></ItemGroup></Project>");
        var items = Scan();

        // WaitAsync throws TimeoutException when matching stalls, which fails the test.
        var reason = await Task.Run(() => items.DescribeRefusal(PathOf("src/Lib/" + new string('a', 200) + ".cs"))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Null(reason);
    }

    [Fact]
    public void OverlongItemValue_RefusesItsDirectory_WithoutBuildingAPattern()
    {
        var value = new string('?', MsBuildCompileItems.MaxValueLength + 1);
        _workspace.WriteFile("src/App/App.csproj", $"<Project><ItemGroup><Compile Include=\"{value}\" /></ItemGroup></Project>");

        Assert.Equal(Reason, Scan().DescribeRefusal(PathOf("src/App/Bar.cs")));
    }

    [Fact]
    public void DirectoryLinkLeavingTheRoot_IsNotFollowed_AndRefusesItsParent()
    {
        // A project behind the link could name any file, and the scan does not read it.
        using var outside = new TempWorkspace();
        outside.WriteFile("Shared.projitems", "<Project><ItemGroup><Compile Include=\"Unrelated.cs\" /></ItemGroup></Project>");
        Directory.CreateDirectory(PathOf("src/App"));
        Directory.CreateSymbolicLink(PathOf("src/App/Shared"), outside.Root);

        var items = Scan();

        Assert.Equal("cannot read MSBuild Compile items in src/App/Shared; manual split required", items.DescribeRefusal(PathOf("src/App/Bar.cs")));
        Assert.Null(items.DescribeRefusal(PathOf("src/Lib/Bar.cs")));
    }

    [Fact]
    public void DirectoryLinkInsideTheRoot_RefusesNothing()
    {
        // The walk reads the target by its real path, so the link adds nothing unseen.
        _workspace.WriteFile("src/Real/Real.csproj", "<Project><ItemGroup><Compile Include=\"Unrelated.cs\" /></ItemGroup></Project>");
        Directory.CreateSymbolicLink(PathOf("src/App"), PathOf("src/Real"));

        Assert.Null(Scan().DescribeRefusal(PathOf("src/Bar.cs")));
    }

    [Fact]
    public void UnlistableDirectory_EndsTheScan()
    {
        // Root can list anything, so the permission has no effect there.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var locked = PathOf("src/Locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(Scan);

            Assert.StartsWith($"cannot scan {locked} for MSBuild files: ", error.Message, StringComparison.Ordinal);
            Assert.EndsWith("Make that directory readable; --repo-root must stay the repository root.", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private MsBuildCompileItems Scan() => MsBuildCompileItems.Scan(_workspace.Root);

    private string PathOf(string relativePath) => Path.Combine(_workspace.Root, relativePath);
}
