using System.IO;
using System.Linq;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The 0.2.0 false failures came from parse settings and an error filter written inline.
// SourceParsing now owns both. If a second copy appears, input and outputs can again be
// judged by different rules, so these tests fail the build when one does. Decision 33.
public sealed class ParseRuleOwnershipTests
{
    private static readonly string SourceDir = Path.Combine(RepositoryPaths.Root(), "src", "AgenticRoslynTool");

    [Theory]
    [InlineData("CSharpParseOptions.Default")]
    [InlineData("new CSharpParseOptions")]
    [InlineData("DiagnosticSeverity.Error")]
    [InlineData("GetDiagnostics(")]
    [InlineData(".WithFeatures(")]
    [InlineData("CSharpParseOptions parseOptions")]
    [InlineData(".WithPreprocessorSymbols(")]
    [InlineData("LanguageVersion.")]
    public void OnlySourceParsing_DefinesTheParseRule(string token)
    {
        var offenders = Directory.GetFiles(SourceDir, "*.cs")
            .Where(path => Path.GetFileName(path) != "SourceParsing.cs")
            .Where(path => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(offenders.Length == 0, $"{token} belongs in SourceParsing.cs only. Found in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EveryParse_UsesSourceParsingOptions()
    {
        // A ParseText call with no options parses with C# defaults, not the tool's rule.
        // Read whole files and match each call's argument list, so a call split across lines
        // is still seen. No method takes the options as a parameter, so every call must name
        // SourceParsing.Options itself.
        var offenders = Directory.GetFiles(SourceDir, "*.cs")
            .SelectMany(path => System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(path), @"\bParse(Text|CompilationUnit|SyntaxTree)\((?<args>[^;]*)")
                .Select(m => (File: Path.GetFileName(path), Args: m.Groups["args"].Value)))
            .Where(x => !x.Args.Contains("SourceParsing.Options", StringComparison.Ordinal))
            .Select(x => $"{x.File}: {x.Args.Trim()}")
            .ToArray();

        Assert.True(offenders.Length == 0, "ParseText without the tool's options: " + string.Join(" | ", offenders));
    }
}
