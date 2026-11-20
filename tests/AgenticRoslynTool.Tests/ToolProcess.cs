using System.Diagnostics;

namespace AgenticRoslynTool.Tests;

// Runs the built tool as a separate process, the way an agent does. A contract that lives in
// Program.cs, such as exit codes, the stdout and stderr split, or what reaches the manifest,
// is invisible to a test that calls the library directly.
internal static class ToolProcess
{
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
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, standardOutput.Result, standardError);
    }
}
