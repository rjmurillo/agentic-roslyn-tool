using System.Diagnostics;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The renames phase runs git mv, which needs a repository and a tracked file. Nothing is
// committed, so no author identity is required on the machine running the test.
internal static class GitFixture
{
    public static void InitAndTrack(string root, params string[] trackedFiles)
    {
        Git(root, "init", "--quiet");
        Git(root, ["add", "--", .. trackedFiles]);
    }

    private static void Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start git");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }
}
