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
/// that drops one is refused instead. Two earlier guards run first.
/// <see cref="DirectiveAnalyzer.AnalyzeDirectiveSafety"/> refuses an <c>#if</c> group unless
/// exactly one type overlaps it or exactly one type sits wholly inside it.
/// <see cref="DirectiveAnalyzer.ProducesEmptyDirectiveShell"/> refuses a split that leaves an
/// <c>#if</c> or <c>#region</c> group holding only blank lines and lines of that directive
/// family. A <c>#nullable</c> line counts as content there, so it does not empty a group.
/// Every group the two let through reaches this guard, wherever it sits in or around a
/// type. Do not describe that set as a list of shapes; review found lists incomplete twice.
/// See decision 31.
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
            var carved = OutputBuilder.Carve(root, types, file);
            var kept = TopLevelType.FindMatch(carved, file.Type).Node;
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
