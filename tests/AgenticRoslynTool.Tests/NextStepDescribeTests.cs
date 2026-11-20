using Xunit;

namespace AgenticRoslynTool.Tests;

// The Next: line on standard error. Decision 27.
public sealed class NextStepDescribeTests
{
    [Fact]
    public void Describe_SingleQuotesSpacesAndBackslashes_AndSaysToCommitAfterRenames()
    {
        // Single quotes are literal in bash, zsh, and PowerShell. Double quotes still expand $.
        IReadOnlyList<string> args = ["split-types", "--phase", "content", "--input", "/r/in put.txt", "--manifest", @"C:\repo\plan.csv"];

        Assert.Equal(@"Next: run <tool command> split-types --phase content --input '/r/in put.txt' --manifest 'C:\repo\plan.csv'", NextStep.Describe(args, Phase.Plan));
        Assert.StartsWith("Next: commit the renames, then run ", NextStep.Describe(args, Phase.Renames), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("// a \"quoted\" header")]
    [InlineData("// line one\n// line two")]
    [InlineData("// $Corp")]
    [InlineData("// `whoami`")]
    [InlineData("// R&D")]
    [InlineData("// it's ours")]
    [InlineData("=x")]
    [InlineData("@gen")]
    [InlineData("a,b")]
    public void Describe_FallsBackToASentence_WhenNoQuotingIsSafe(string header)
    {
        IReadOnlyList<string> args = ["split-types", "--phase", "content", "--require-header", header];

        var line = NextStep.Describe(args, Phase.Plan);

        Assert.Equal("Next: run --phase content with the same options. --json prints the exact arguments as nextArgs.", line);
        Assert.DoesNotContain('\n', line);
    }
}
