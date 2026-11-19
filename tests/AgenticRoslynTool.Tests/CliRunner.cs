using System.Diagnostics;
using System.IO;

namespace AgenticRoslynTool.Tests;

// Runs the real executable. Which file a run writes is decided in Program.cs, so a test
// that calls the library directly cannot see it.
internal static class CliRunner
{
    public static CliResult Run(params string[] args)
    {
        var toolPath = typeof(Options).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
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
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, standardOutput, standardError.GetAwaiter().GetResult());
    }
}
