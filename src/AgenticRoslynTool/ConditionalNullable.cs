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
/// that drops one is refused instead. Two earlier guards run first:
/// <see cref="DirectiveAnalyzer.AnalyzeDirectiveSafety"/> refuses a group that no single type
/// owns, and <see cref="DirectiveAnalyzer.ProducesEmptyDirectiveShell"/> refuses a split that
/// leaves an empty group. Every group they let through reaches this
/// guard, wherever it sits in or around a type. See decision 30.
/// </remarks>
internal static class ConditionalNullable
{
    /// <summary>
    /// Reports whether any planned output loses a conditional <c>#nullable</c> directive that
    /// comes before its type in the source.
    /// </summary>
    internal static bool IsDropped(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, SplitPlan plan)
    {
        // A carve only removes directives. With none of these in the source, no output can
        // lose one, so the per-output carve is skipped for almost every file.
        if (CountBefore(root, root.FullSpan.End) == 0)
        {
            return false;
        }

        foreach (var file in plan.Files)
        {
            var carved = OutputBuilder.Carve(root, types, file.Type);
            var kept = TopLevelType.FindByKey(carved, file.Type.Key).Node;
            if (CountBefore(carved, kept.SpanStart) != CountBefore(root, file.Type.Node.SpanStart))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts the <c>#nullable</c> directives before <paramref name="position"/> that sit
    /// inside an <c>#if</c> group, in any branch. Roslyn parses a directive in a branch that
    /// is off as an inactive directive, not as disabled text, so one walk over the
    /// directives sees both kinds.
    /// </summary>
    internal static int CountBefore(CSharpSyntaxNode root, int position)
    {
        var depth = 0;
        var count = 0;
        foreach (var directive in SourceDirectives.Before(root, position))
        {
            switch (directive.Kind())
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
            }
        }

        return count;
    }
}
