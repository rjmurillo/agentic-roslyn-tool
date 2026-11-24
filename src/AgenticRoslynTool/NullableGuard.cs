using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Decides whether a split could leave some code in a new file with a nullable context
/// that <see cref="NullableDirectives.Restore"/> cannot put back. Returns a verdict, never
/// acts on one.
/// </summary>
/// <remarks>
/// <para>
/// A <c>#nullable</c> directive is movable when it starts at or after the start of the first
/// span a split can remove: a type with its leading trivia, or an <c>[assembly: ...]</c>
/// attribute list, which only the kept file keeps. That over-approximates. A directive
/// between an attribute list and the namespace stays in every new file, yet counts as
/// movable. A directive before the first such span sits with the usings, and every new file
/// keeps it. A file with no movable directive splits as it did before this guard.
/// </para>
/// <para>
/// With a movable directive, two shapes are refused.
/// </para>
/// <list type="number">
/// <item>The file holds any <c>#if</c>. <see cref="NullableDirectives.StateAt"/> folds the parse
/// with no symbols defined. A file with no <c>#if</c> has one build, so that fold is the
/// compiler's answer for <c>#nullable</c> directives. With an <c>#if</c>, some build can read
/// directives, definitions, or whole types the fold never saw. Review found four such shapes
/// in turn, each past a narrower rule, so the guard refuses the <c>#if</c> itself. In such a
/// file a directive can even hide from the parse: a comment or a string opened under
/// <c>#if</c> swallows a <c>#nullable</c> line in one build that another build reads. So with an
/// <c>#if</c>, a movable directive is any line at or after that span that reads as
/// <c>#nullable</c>. A directive must start its own line, and lines are the same in every
/// build.</item>
/// <item>The directive comes before any <c>using</c> directive. <c>Restore</c> restates the context
/// above the type it keeps. A <c>using</c> after the directive sits above that type, in a later
/// namespace or after an <c>[assembly: ...]</c> list, so a <c>string?</c> in a using alias would
/// lose the context.</item>
/// </list>
/// <para>
/// Earlier checks run first and refuse some of these files with their own reasons. See
/// decision 31.
/// </para>
/// </remarks>
internal static partial class NullableGuard
{
    /// <summary>
    /// Reports whether <paramref name="root"/> holds a movable <c>#nullable</c> directive and,
    /// beside it, any <c>#if</c> directive, or a <c>using</c> directive after it.
    /// </summary>
    internal static bool Refuses(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types)
    {
        var removable = types.Select(type => type.Node.FullSpan.Start)
            .Concat(root.AttributeLists.Select(list => list.FullSpan.Start))
            .Min();
        var directives = SourceDirectives.Before(root, root.FullSpan.End).ToArray();
        if (directives.Any(directive => directive.Kind() == SyntaxKind.IfDirectiveTrivia))
        {
            return NullableLine().Matches(root.ToFullString()).Any(line => line.Index >= removable);
        }

        var movable = directives.FirstOrDefault(directive => directive.Kind() == SyntaxKind.NullableDirectiveTrivia && directive.SpanStart >= removable);
        return movable is not null
            && root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(directive => directive.SpanStart > movable.SpanStart);
    }

    [GeneratedRegex(@"^[ \t]*#[ \t]*nullable\b", RegexOptions.Multiline)]
    private static partial Regex NullableLine();
}
