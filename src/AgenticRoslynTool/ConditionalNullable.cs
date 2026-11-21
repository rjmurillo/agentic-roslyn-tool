using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Decides whether a split would drop a <c>#nullable</c> directive whose effect depends on
/// preprocessor symbols. Returns a verdict, never acts on one.
/// </summary>
/// <remarks>
/// <see cref="NullableDirectives.StateAt"/> folds the parse with no symbols defined. A
/// <c>#nullable</c> under <c>#if</c> can set a different context in a build that defines a
/// symbol, so restating it as one unconditional line would be wrong for some build. A split
/// that drops one is refused instead. An <c>#if</c> group outside a type body is already
/// refused by <see cref="DirectiveAnalyzer.AnalyzeDirectiveSafety"/>, so the group this
/// meets sits inside a type body. See decision 30.
/// </remarks>
internal static class ConditionalNullable
{
    /// <summary>
    /// Reports whether any planned output loses a conditional <c>#nullable</c> directive that
    /// comes before its type in the source.
    /// </summary>
    internal static bool IsDropped(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, SplitPlan plan)
    {
        foreach (var file in plan.Files)
        {
            var remove = types.Where(t => !ReferenceEquals(t, file.Type)).Select(t => t.Node).ToArray();
            var newRoot = root.RemoveNodes(remove, SyntaxRemoveOptions.KeepUnbalancedDirectives) ?? root;
            var carved = TopLevelType.Find(newRoot).First(t => StringComparer.Ordinal.Equals(t.Key, file.Type.Key)).Node;
            if (CountBefore(newRoot, carved.SpanStart) != CountBefore(root, file.Type.Node.SpanStart))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts the <c>#nullable</c> directives before <paramref name="position"/> whose effect
    /// depends on preprocessor symbols: an active one inside an <c>#if</c> group, and a
    /// <c>#nullable</c> line in the text of a branch that is off.
    /// </summary>
    internal static int CountBefore(SyntaxNode root, int position)
    {
        var depth = 0;
        var count = 0;
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.SpanStart >= position)
            {
                break;
            }

            switch (trivia.Kind())
            {
                case SyntaxKind.IfDirectiveTrivia:
                    depth++;
                    break;
                case SyntaxKind.EndIfDirectiveTrivia:
                    depth--;
                    break;
                case SyntaxKind.NullableDirectiveTrivia when depth > 0:
                    count++;
                    break;
                case SyntaxKind.DisabledTextTrivia:
                    count += trivia.ToString().Split('\n').Count(line => line.TrimStart().StartsWith("#nullable", StringComparison.Ordinal));
                    break;
            }
        }

        return count;
    }
}
