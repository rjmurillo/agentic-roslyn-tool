using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A failure from OutputVerifier means the tool refused a split it built itself. Every such
// row starts with one prefix, so an agent can tell it from a problem in the input and never
// edits source to get past it. Decision 33.
public sealed class OutputCheckPrefixTests
{
    [Fact]
    public void VerifierFailure_StartsWithTheOutputCheckPrefix()
    {
        // A header that is not a comment makes every output fail to parse, which is the
        // cheapest real verifier failure to provoke through a full plan run.
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("Stuff.cs", "namespace N;\n\npublic class Order2 { }\n\npublic class Line { }\n");
        var options = new Options(ws.WriteInputList(path), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, "this is not a comment");

        var row = Assert.Single(new FileSplitter(options).Run());

        Assert.Equal("failed", row.Status);
        Assert.StartsWith(FileSplitter.OutputCheckFailedPrefix, row.Reason);
        Assert.Contains("does not parse", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void InputSyntaxError_StartsWithTheSyntaxErrorsPrefix()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("Broken.cs", "namespace N;\n\ninternal class Broken { void M( }\ninternal class Other { }\n");
        var options = new Options(ws.WriteInputList(path), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null);

        var row = Assert.Single(new FileSplitter(options).Run());

        Assert.Equal("failed", row.Status);
        Assert.StartsWith(FileSplitter.SyntaxErrorsPrefix, row.Reason);
    }
}
