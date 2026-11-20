using System.Diagnostics;

namespace AgenticRoslynTool.Tests;

// Runs the real executable. Which file a run writes is decided in Program.cs, so a test
// that calls the library directly cannot see it. Exit codes and the stdout and stderr split
// live there too.
internal static class CliRunner
{
    // Far above any real run in this suite. A child that is still alive after this is hung,
    // and a hung child must fail the test, not stall the CI or release job with no limit.
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    public static CliResult Run(params string[] args) => RunIn(Path.GetTempPath(), args);

    public static CliResult RunIn(string workingDirectory, params string[] args)
    {
        var toolPath = typeof(Options).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(toolPath);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;

        // Standard error is drained on its own task. Reading one stream to the end before
        // touching the other blocks forever once the unread one fills its pipe.
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        WaitOrKill(process, $"agentic-roslyn-tool {string.Join(' ', args)}");
        return new CliResult(process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    // Shared with GitFixture so every child process in the suite has the same limit.
    public static void WaitOrKill(Process process, string description)
    {
        if (!process.WaitForExit(Limit))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{description} did not exit within {Limit.TotalMinutes} minutes and was killed.");
        }

        // The timed overload can return before redirected output is fully read.
        process.WaitForExit();
    }
}
