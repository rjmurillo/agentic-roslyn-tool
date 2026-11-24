using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Lists every build a source file's <c>#if</c> conditions can describe, as sets of defined
/// symbols, so a test can check each one.
/// </summary>
internal static class PreprocessorSymbolSets
{
    private const int MaxSymbols = 6;

    /// <summary>Every subset of the identifiers the source's <c>#if</c> and <c>#elif</c> conditions name.</summary>
    internal static IEnumerable<string[]> Of(string source, CSharpParseOptions parse)
    {
        var root = CSharpSyntaxTree.ParseText(source, parse).GetCompilationUnitRoot();
        var names = root.DescendantTrivia(descendIntoTrivia: true)
            .Select(trivia => trivia.GetStructure())
            .OfType<ConditionalDirectiveTriviaSyntax>()
            .SelectMany(directive => directive.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            .Select(name => name.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(names.Length <= MaxSymbols, $"source names {names.Length} #if symbols; the oracle checks at most {MaxSymbols}");
        for (var mask = 0; mask < 1 << names.Length; mask++)
        {
            yield return names.Where((_, bit) => (mask & (1 << bit)) != 0).ToArray();
        }
    }
}
