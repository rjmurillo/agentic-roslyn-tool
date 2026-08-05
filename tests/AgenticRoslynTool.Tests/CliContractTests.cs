using System.Diagnostics;
using System.IO;
using System.Text.Json;
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

    // Guards the whole point of --json: exactly one parseable document on stdout, with the
    // manifest path and summary kept on stderr so a pipe into a parser stays clean.
    [Fact]
    public void JsonRun_PutsOnlyTheReportOnStandardOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Two.cs"), "class A { }\nclass B { }\n");
            var manifest = Path.Combine(directory, "manifest.csv");

            var result = Run("split-types", "--input", directory, "--manifest", manifest, "--dry-run", "--json");

            Assert.Equal(0, result.ExitCode);

            using var document = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal("plan", document.RootElement.GetProperty("phase").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("total").GetInt32());

            Assert.DoesNotContain("Manifest:", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Manifest:", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("input(s)", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // "help" in a value position is a path, not a request for usage. Scanning every token
    // made `--input help` exit 0 with usage and touch nothing, which a caller that cannot
    // read the screen sees as a successful no-op run.
    [Fact]
    public void HelpInAValuePosition_IsTreatedAsAValue()
    {
        var result = Run("split-types", "--input", "help");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
        Assert.DoesNotContain("Usage:", result.StandardOutput, StringComparison.Ordinal);
    }

    // Any run-time failure owes the caller one error line and exit 3. A manifest with a
    // duplicate header name used to escape the filtered catch and print a stack trace.
    [Fact]
    public void UnexpectedRunFailure_StillReportsOneErrorLineAndExitsThree()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = Path.Combine(directory, "manifest.csv");
            File.WriteAllText(manifest, "originalPath,keptPath,status,status\na,b,split,split\n");
            var list = Path.Combine(directory, "inputs.txt");
            File.WriteAllText(list, string.Empty);

            var result = Run("split-types", "--input", list, "--manifest", manifest, "--phase", "content");

            Assert.Equal(3, result.ExitCode);
            Assert.StartsWith("error: ", result.StandardError.Trim(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
