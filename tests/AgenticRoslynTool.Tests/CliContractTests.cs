using System.Diagnostics;
using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The exit codes and the stdout/stderr split are the contract an agent depends on. These
// run the real executable, because the defect they guard against lives in Program.cs and
// is invisible to a test that calls the library directly.
public sealed class CliContractTests
{
    [Fact]
    public void NoArguments_ReportsTheErrorOnStandardErrorAndExitsTwo()
    {
        var result = Run();

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
        Assert.Contains("error: no command given.", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCommand_KeepsUsageOffStandardOutput()
    {
        var result = Run("bogus-command");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
        Assert.Contains("unknown command", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Usage:", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOption_KeepsUsageOffStandardOutput()
    {
        var result = Run("split-types", "--input", "list.txt", "--bogus");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
        Assert.Contains("error: Unknown argument: --bogus", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingInput_ExitsTwoWithoutAStackTrace()
    {
        var result = Run("split-types");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("error: Missing --input.", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_GoesToStandardOutputAndExitsZero()
    {
        // Both positions work: help is the one request that must never depend on the rest
        // of the command line being valid.
        foreach (var args in new[] { new[] { "--help" }, ["split-types", "--help"], ["split-types", "help"] })
        {
            var result = Run(args);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MissingInputFile_ExitsThreeWithoutAStackTrace()
    {
        var result = Run("split-types", "--input", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".txt"));

        Assert.Equal(3, result.ExitCode);
        Assert.StartsWith("error: ", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunCombinedWithPhase_IsRejectedInEitherOrder()
    {
        // These both set the phase, so the last one on the line used to win silently. That
        // made argument order the difference between a manifest and a source rewrite.
        Assert.Throws<ArgumentException>(() => Options.Parse(["--input", "list.txt", "--dry-run", "--phase", "content"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--input", "list.txt", "--phase", "content", "--dry-run"]));
    }

    [Fact]
    public void Version_WorksAfterTheVerbAsWellAsBeforeIt()
    {
        var atVerb = Run("--version");
        var afterVerb = Run("split-types", "--version");

        Assert.Equal(0, atVerb.ExitCode);
        Assert.Equal(0, afterVerb.ExitCode);
        Assert.Equal(atVerb.StandardOutput.Trim(), afterVerb.StandardOutput.Trim());
        Assert.NotEqual(string.Empty, atVerb.StandardOutput.Trim());
    }

    private static CliResult Run(params string[] args)
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
