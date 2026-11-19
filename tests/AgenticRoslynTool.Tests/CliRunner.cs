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
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, standardOutput, standardError);
    }
}
