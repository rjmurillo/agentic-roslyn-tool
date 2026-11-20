using System.ComponentModel;
using System.Diagnostics;

namespace AgenticRoslynTool;

/// <summary>
/// Runs one git command with a time limit. Owns the process lifecycle, so
/// <see cref="WorkspaceWriter"/> only decides what to move.
/// </summary>
/// <remarks>
/// Each argument is passed on its own, so no quote or backslash in a path can be read as
/// argument syntax. Both output streams are read while git runs, so a full pipe cannot
/// stall it. A git that runs past the limit is killed with its children. Every wait after
/// that has its own short bound, so a stuck descendant holding a pipe cannot hang the run.
/// </remarks>
internal static class GitCommand
{
    // Bounds every wait on a git that already exited or was killed.
    private static readonly TimeSpan StreamDrainLimit = TimeSpan.FromSeconds(5);

    /// <summary>Runs <c>git</c> with the given arguments in <paramref name="repoRoot"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when git cannot be started or exits with a non-zero code.</exception>
    /// <exception cref="TimeoutException">Thrown when git does not exit within <paramref name="timeout"/>.</exception>
    internal static void Run(string repoRoot, TimeSpan timeout, params string[] arguments)
    {
        var name = "git " + arguments[0];
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"failed to start {name}");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            var stopped = Kill(process);
            throw new TimeoutException(DescribeTimeout(name, timeout, repoRoot, stopped, Drain(error)));
        }

        // On success nothing reads what git printed, so it is dropped on purpose.
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{name} failed (exit {process.ExitCode}): " + Drain(error) + Drain(output));
        }
    }

    private static string DescribeTimeout(string name, TimeSpan timeout, string repoRoot, bool stopped, string stderr)
    {
        var outcome = stopped ? "was killed" : "could not be stopped and may still be running";
        var message = $"{name} did not exit within {timeout.TotalSeconds} seconds and {outcome}. " +
            $"A killed git can leave an index.lock file in the git directory of {repoRoot} " +
            "(git rev-parse --git-dir prints it) and a half-finished move. " +
            "Remove the lock and check git status before the next run.";
        return stderr.Length == 0 ? message : $"{message} git said: {stderr}";
    }

    // Returns whether the process is gone. Kill throws when the process exits on its own
    // first, or when part of the tree cannot be ended. The timeout message stands either way.
    private static bool Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
        }

        return process.WaitForExit(StreamDrainLimit);
    }

    // A read that faults or outlives the bound leaves its stream out of the message.
    private static string Drain(Task<string> stream)
    {
        try
        {
            stream.Wait(StreamDrainLimit);
        }
        catch (AggregateException)
        {
        }

        return stream.IsCompletedSuccessfully ? stream.Result.Trim() : string.Empty;
    }
}
