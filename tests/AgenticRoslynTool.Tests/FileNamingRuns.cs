using System.Diagnostics;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Shared steps for the --file-naming tests: run one phase under a chosen convention, list
// the C# files a workspace holds, and start git or the built tool as a real process.
internal static class FileNamingRuns
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

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

    // Runs one phase the way Program does: only the plan phase writes the manifest.
    public static FileResult[] RunPhase(TempWorkspace ws, string listPath, string manifestPath, Phase phase, FileNamingConvention naming, CollisionNaming collisionNaming = CollisionNaming.FileFirst)
    {
        var options = new Options(listPath, manifestPath, ws.Root, phase, RequiredHeader: null, CollisionNaming: collisionNaming, FileNaming: naming);
        var rows = new FileSplitter(options).Run();
        if (phase == Phase.Plan)
        {
            ManifestWriter.Write(rows, manifestPath);
        }

        return rows.ToArray();
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

        // Both streams are read on their own tasks, so a full pipe cannot block the child and
        // the wait below is the only thing that can run long. It is bounded: a child that
        // hangs is killed and fails the test instead of stalling the run.
        using var process = Process.Start(start)!;
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{fileName} {string.Join(' ', args)} did not exit within {ProcessTimeout.TotalSeconds} seconds.");
        }

        process.WaitForExit();
        return new CliResult(process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }
}
