using Xunit;

namespace AgenticRoslynTool.Tests;

// The Next: line on standard error. Decision 25.
public sealed class NextStepDescribeTests
{
    [Fact]
    public void Describe_QuotesSpaces_AndSaysToCommitAfterRenames()
    {
        IReadOnlyList<string> args = ["split-types", "--phase", "content", "--input", "/r/in put.txt"];

        Assert.Equal("Next: run <tool command> split-types --phase content --input \"/r/in put.txt\"", NextStep.Describe(args, Phase.Plan));
        Assert.StartsWith("Next: commit the renames, then run ", NextStep.Describe(args, Phase.Renames), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("// a \"quoted\" header")]
    [InlineData("// line one\n// line two")]
    public void Describe_FallsBackToASentence_WhenNoQuotingIsSafe(string header)
    {
        IReadOnlyList<string> args = ["split-types", "--phase", "content", "--require-header", header];

        var line = NextStep.Describe(args, Phase.Plan);

        Assert.Equal("Next: run --phase content with the same options. --json prints the exact arguments as nextArgs.", line);
        Assert.DoesNotContain('\n', line);
    }
}
