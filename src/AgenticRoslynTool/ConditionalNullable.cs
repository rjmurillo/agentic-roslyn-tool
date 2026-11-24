using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Decides whether some type's nullable context in the source depends on preprocessor
/// symbols. Returns a verdict, never acts on one.
/// </summary>
/// <remarks>
/// <see cref="NullableDirectives.StateAt"/> folds the parse with no symbols defined, and
/// <see cref="NullableDirectives.Restore"/> restates what it finds as unconditional lines.
/// That is exact only when every <c>#nullable</c> before a type is unconditional, because
/// then the type has one context in every build. So this refuses a file where a
/// <c>#nullable</c> under <c>#if</c> starts before the last type.
/// <para>
/// Comparing what each output loses is not enough. A conditional directive in a type's own
/// leading trivia travels with it, so no output loses it, yet an unconditional directive
/// above it can be lost. A build that defines the symbol then sees only the lost one in the
/// source. Review found that wrong write after the counting rule had passed five rounds.
/// </para>
/// <para>
/// The older guards in <see cref="DirectiveAnalyzer"/> run first and refuse some of these
/// files with their own reasons. A conditional directive inside the last type, after its
/// start, sets context only for that type's members and travels with it, so it is allowed.
/// </para>
/// See decision 31.
/// </remarks>
internal static class ConditionalNullable
{
    /// <summary>
    /// Reports whether a <c>#nullable</c> directive under <c>#if</c> starts before the start
    /// of the last type in <paramref name="types"/>.
    /// </summary>
    internal static bool AffectsAType(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types)
    {
        return CountBefore(root, types.Max(type => type.Node.SpanStart)) > 0;
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
