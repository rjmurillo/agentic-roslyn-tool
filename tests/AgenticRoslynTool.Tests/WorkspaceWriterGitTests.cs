using System.Diagnostics;
using Xunit;

namespace AgenticRoslynTool.Tests;

// RunGitMove against a real git. The hang test swaps the index for a named pipe, so git
// blocks on open with no writer. That is a real git hang, not a fake executable.
public sealed class WorkspaceWriterGitTests : IDisposable
{
    private static readonly TimeSpan OuterLimit = TimeSpan.FromSeconds(60);

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void RunGitMove_PathWithDoubleQuote_MovesTheFile()
    {
        // Windows forbids a double quote in a file name.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var source = _workspace.WriteFile("Has\"Quote.cs", "class A { }\n");
        GitFixture.InitAndTrack(_workspace.Root, "Has\"Quote.cs");
        var target = Path.Combine(_workspace.Root, "Kept.cs");

        new WorkspaceWriter(_workspace.Root).RunGitMove(source, target);

        Assert.False(File.Exists(source));
        Assert.Equal("class A { }\n", File.ReadAllText(target));
    }

    [Fact]
    public void RunGitMove_PathWithSpaces_MovesTheFile()
    {
        var source = _workspace.WriteFile(Path.Combine("a dir", "Old Name.cs"), "class A { }\n");
        GitFixture.InitAndTrack(_workspace.Root, "a dir/Old Name.cs");
        var target = Path.Combine(_workspace.Root, "a dir", "New Name.cs");

        new WorkspaceWriter(_workspace.Root).RunGitMove(source, target);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task RunGitMove_GitHangs_KillsGitAndThrowsATimeout()
    {
        // Named pipes in the file system are a Unix feature.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var source = _workspace.WriteFile("A.cs", "class A { }\n");
        GitFixture.InitAndTrack(_workspace.Root, "A.cs");
        var index = Path.Combine(_workspace.Root, ".git", "index");
        File.Move(index, index + ".bak");
        RunMkfifo(index);
        var writer = new WorkspaceWriter(_workspace.Root, TimeSpan.FromSeconds(2));

        var clock = Stopwatch.StartNew();
        var move = Task.Run(() => writer.RunGitMove(source, Path.Combine(_workspace.Root, "B.cs")));
        var finished = await Task.WhenAny(move, Task.Delay(OuterLimit)) == move;
        clock.Stop();
        if (!finished)
        {
            await ReleaseBlockedReaderAsync(index, move);
        }

        Assert.True(finished, $"RunGitMove did not return within {OuterLimit.TotalSeconds} seconds.");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"RunGitMove took {clock.Elapsed}.");
        var error = await Assert.ThrowsAsync<TimeoutException>(() => move);
        Assert.Contains("git mv did not exit within 2 seconds", error.Message, StringComparison.Ordinal);
        Assert.Contains("index.lock", error.Message, StringComparison.Ordinal);
    }

    // The old unbounded wait leaves git blocked on the pipe. Opening the write end lets git
    // read an empty index and exit, so a failing run does not leave a stuck process behind.
    private static async Task ReleaseBlockedReaderAsync(string fifo, Task move)
    {
        var open = Task.Run(() => File.OpenWrite(fifo).Dispose());
        await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(10)));
        await Task.WhenAny(move, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private static void RunMkfifo(string path)
    {
        var start = new ProcessStartInfo("mkfifo") { RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start mkfifo");
        var error = process.StandardError.ReadToEndAsync();
        CliRunner.WaitOrKill(process, "mkfifo");
        Assert.True(process.ExitCode == 0, $"mkfifo failed: {error.GetAwaiter().GetResult()}");
    }
}
