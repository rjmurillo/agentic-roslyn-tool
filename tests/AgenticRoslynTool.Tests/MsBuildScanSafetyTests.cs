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
        Directory.CreateDirectory(PathOf("src/App"));
        using (var stream = File.Create(PathOf("src/App/App.csproj")))
        {
            // Sparse on most file systems, so the test writes almost nothing.
            stream.SetLength(MsBuildCompileItems.MaxFileBytes + 1);
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
    public void PatternWithManyWildcards_MatchesInLinearTime()
    {
        // A backtracking engine takes time exponential in the wildcard count on a name
        // that almost matches. This one would not finish within the test run.
        var wildcards = string.Concat(Enumerable.Repeat("*a", 40)) + "*b.cs";
        _workspace.WriteFile("src/App/App.csproj", $"<Project><ItemGroup><Compile Include=\"{wildcards}\" /></ItemGroup></Project>");
        var items = Scan();

        var watch = Stopwatch.StartNew();
        var reason = items.DescribeRefusal(PathOf("src/Lib/" + new string('a', 200) + ".cs"));
        watch.Stop();

        Assert.Null(reason);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"matching took {watch.Elapsed}");
    }

    private MsBuildCompileItems Scan() => MsBuildCompileItems.Scan(_workspace.Root);

    private string PathOf(string relativePath) => Path.Combine(_workspace.Root, relativePath);
}
