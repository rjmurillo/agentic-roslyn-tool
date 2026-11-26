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

    /// <summary>Every subset of the identifiers the source's <c>#if</c> and <c>#elif</c> conditions name, in any build.</summary>
    /// <remarks>
    /// A comment or string opened in one build can hide a condition from the parse of another.
    /// So the names are collected again under every set found so far, until no new one appears.
    /// </remarks>
    internal static IEnumerable<string[]> Of(string source, CSharpParseOptions parse)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var found = NamesIn(source, parse);
        while (!names.IsSupersetOf(found))
        {
            names.UnionWith(found);
            Assert.True(names.Count <= MaxSymbols, $"source names {names.Count} #if symbols; the oracle checks at most {MaxSymbols}");
            found = Subsets(names.ToArray()).SelectMany(set => NamesIn(source, parse.WithPreprocessorSymbols(set))).ToArray();
        }

        return Subsets(names.ToArray());
    }

    private static string[] NamesIn(string source, CSharpParseOptions parse)
    {
        var root = CSharpSyntaxTree.ParseText(source, parse).GetCompilationUnitRoot();
        return root.DescendantTrivia(descendIntoTrivia: true)
            .Select(trivia => trivia.GetStructure())
            .OfType<ConditionalDirectiveTriviaSyntax>()
            .SelectMany(directive => directive.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            .Select(name => name.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string[]> Subsets(string[] names)
    {
        for (var mask = 0; mask < 1 << names.Length; mask++)
        {
            yield return names.Where((_, bit) => (mask & (1 << bit)) != 0).ToArray();
        }
    }
}
