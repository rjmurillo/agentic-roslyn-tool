using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Shared steps for the --file-naming tests: run one phase under a chosen convention, list
// the C# files a workspace holds, and start git or the built tool as a real process.
internal static class FileNamingRuns
{
    public const string AWithGenericFoo = "namespace N;\n\npublic class A\n{\n}\n\npublic class Foo<T>\n{\n}\n";

    public static string[] CsFiles(TempWorkspace ws)
    {
        return Directory.GetFiles(ws.Root, "*.cs").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray();
    }

    public static FileResult[] RunPlanThenContent(TempWorkspace ws, string listPath, string manifestPath, FileNamingConvention planNaming, FileNamingConvention contentNaming)
    {
        RunPhase(ws, listPath, manifestPath, Phase.Plan, planNaming);
        return RunPhase(ws, listPath, manifestPath, Phase.Content, contentNaming);
    }

    // Runs one phase and writes its manifest rows back, as Program does between processes.
    public static FileResult[] RunPhase(TempWorkspace ws, string listPath, string manifestPath, Phase phase, FileNamingConvention naming)
    {
        var options = new Options(listPath, manifestPath, ws.Root, phase, RequiredHeader: null, FileNaming: naming);
        var outcome = new FileSplitter(options).Run();
        ManifestWriter.Write(outcome.ManifestRows, manifestPath);
        return outcome.ReportRows.ToArray();
    }

    public static string Git(string workingDirectory, params string[] args)
    {
        var result = RunProcess("git", workingDirectory, args);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)} failed: {result.StandardError}");
        return result.StandardOutput;
    }

    // Runs the built tool as its own process, the way a caller does.
    public static CliResult RunTool(params string[] args)
    {
        return RunProcess("dotnet", Path.GetTempPath(), ["exec", typeof(Options).Assembly.Location, .. args]);
    }

    private static CliResult RunProcess(string fileName, string workingDirectory, string[] args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        // Standard error is read on its own task so a full pipe on one stream cannot
        // block the read of the other.
        using var process = Process.Start(start)!;
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new CliResult(process.ExitCode, standardOutput, standardError.GetAwaiter().GetResult());
    }
}
