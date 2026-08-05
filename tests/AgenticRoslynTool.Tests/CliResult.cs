namespace AgenticRoslynTool.Tests;

/// <summary>What one run of the real executable produced.</summary>
internal sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
