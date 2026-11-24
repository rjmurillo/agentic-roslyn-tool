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
/// A <c>#nullable</c> directive is movable when it starts at or after the first span a split
/// can remove: a type, or an <c>[assembly: ...]</c> attribute list, which only the kept file
/// keeps. A directive before that span sits with the usings and the namespace, and every
/// new file keeps it. A file with no movable directive splits as it did before this guard.
/// </para>
/// <para>
/// With a movable directive, two shapes are refused.
/// </para>
/// <list type="number">
/// <item>The file holds any <c>#if</c>. <see cref="NullableDirectives.StateAt"/> folds the parse
/// with no symbols defined. A file with no <c>#if</c> has one build, so that fold is the
/// compiler's answer for <c>#nullable</c> directives. With an <c>#if</c>, some build can read
/// directives, definitions, or whole types the fold never saw. Review found five such
/// shapes in turn, each past a narrower rule, so the guard refuses the <c>#if</c> itself.</item>
/// <item>The directive comes before a <c>using</c> directive. Such a <c>using</c> sits in a later
/// namespace, after a type that a split removes. <c>Restore</c> restates the context above the
/// type it keeps, below that <c>using</c>, so a <c>string?</c> in a using alias would lose it.</item>
/// </list>
/// <para>
/// Earlier checks run first and refuse some of these files with their own reasons. See
/// decision 31.
/// </para>
/// </remarks>
internal static class NullableGuard
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
        var movable = directives.FirstOrDefault(directive => directive.Kind() == SyntaxKind.NullableDirectiveTrivia && directive.SpanStart >= removable);
        if (movable is null)
        {
            return false;
        }

        return directives.Any(directive => directive.Kind() == SyntaxKind.IfDirectiveTrivia)
            || root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(directive => directive.SpanStart > movable.SpanStart);
    }
}
