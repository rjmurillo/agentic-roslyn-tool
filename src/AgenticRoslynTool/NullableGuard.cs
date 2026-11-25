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
/// With a movable directive, three shapes are refused.
/// </para>
/// <list type="number">
/// <item>The file holds any <c>#if</c>. <see cref="NullableDirectives.StateAt"/> folds the parse
/// with no symbols defined. A file with no <c>#if</c> has one build, so that fold is the
/// compiler's answer for <c>#nullable</c> directives. With an <c>#if</c>, some build can read
/// directives, definitions, or whole types the fold never saw. Review found four such shapes
/// in turn, each past a narrower rule, so the guard refuses the <c>#if</c> itself. In such a
/// file a directive can even hide from the parse: a comment or a string opened under
/// <c>#if</c> swallows a <c>#nullable</c> line in one build that another build reads. So with an
/// <c>#if</c>, a movable directive is any <c>#</c> at or after that span that starts a line
/// and is followed by <c>nullable</c>. A directive must start its own line, and lines are the
/// same in every build. The scan finds lines and whitespace with the compiler's own
/// <see cref="SyntaxFacts.IsNewLine"/> and <see cref="SyntaxFacts.IsWhitespace"/>. A
/// <c>\n</c> and <c>[ \t]</c> scan missed a lone carriage return, a no-break space, and a
/// form feed, each of which the compiler reads as part of a directive line.</item>
/// <item>The directive comes before any <c>using</c> directive. <c>Restore</c> restates the context
/// above the type it keeps. A <c>using</c> after the directive sits above that type, in a later
/// namespace or after an <c>[assembly: ...]</c> list, so a <c>string?</c> in a using alias would
/// lose the context.</item>
/// <item>The file is generated code to the compiler: a name such as <c>Foo.g.cs</c>, or an
/// opening comment that holds <c>&lt;auto-generated</c>. There no directive means disabled,
/// while <c>#nullable restore</c> means the project default. The fold reads both as the
/// default, so <c>Restore</c> can miss a line or add a wrong one. A setting from an
/// <c>.editorconfig</c> is not seen; see AGENTS.md known issues.</item>
/// </list>
/// <para>
/// Earlier checks run first and refuse some of these files with their own reasons. See
/// decision 31.
/// </para>
/// </remarks>
internal static class NullableGuard
{
    /// <summary>
    /// Reason recorded when <see cref="NullableGuard.Refuses"/> refuses a file: a
    /// <c>#nullable</c> directive a split can move shares the file with an <c>#if</c> directive,
    /// comes before a <c>using</c> directive, or sits in a generated file. See decision 31.
    /// </summary>
    internal const string Reason = "a #nullable directive a split can move shares the file with an #if directive, comes before a using directive, or sits in a generated file; manual split required";

    /// <summary>
    /// Reports whether <paramref name="root"/> holds a movable <c>#nullable</c> directive and,
    /// beside it, any <c>#if</c> directive, or a <c>using</c> directive after it, or whether it
    /// holds one in a file the compiler reads as generated.
    /// </summary>
    /// <param name="root">The source tree, parsed at the path the phase reads.</param>
    /// <param name="types">The source's top level types.</param>
    /// <param name="keptPath">The path the kept file will have after the split. A rename to a generated name counts, so the plan refuses before the renames phase moves the file.</param>
    internal static bool Refuses(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, string keptPath)
    {
        var removable = SourceDirectives.RemovableSpans(root, types).Min(span => span.Start);
        var directives = SourceDirectives.Before(root, root.FullSpan.End).ToArray();
        if (directives.Any(directive => directive.Kind() == SyntaxKind.IfDirectiveTrivia))
        {
            return HasNullableLine(root.ToFullString(), removable);
        }

        var movable = directives.FirstOrDefault(directive => directive.Kind() == SyntaxKind.NullableDirectiveTrivia && directive.SpanStart >= removable);
        return movable is not null
            && (CollisionNames.HasGeneratedCodeName(root.SyntaxTree.FilePath)
                || CollisionNames.HasGeneratedCodeName(keptPath)
                || root.GetLeadingTrivia().Any(trivia => trivia.ToString().Contains("<auto-generated", StringComparison.Ordinal) || trivia.ToString().Contains("<autogenerated", StringComparison.Ordinal))
                || root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(directive => directive.SpanStart > movable.SpanStart));
    }

    /// <summary>
    /// Reports whether any line at or after <paramref name="start"/> could be a
    /// <c>#nullable</c> directive in some build: a <c>#</c> with only whitespace before it on its
    /// line, then optional whitespace, then <c>nullable</c>.
    /// </summary>
    private static bool HasNullableLine(string text, int start)
    {
        for (var hash = text.IndexOf('#', start); hash >= 0; hash = text.IndexOf('#', hash + 1))
        {
            if (StartsLine(text, hash) && NamesNullable(text, hash + 1))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsLine(string text, int hash)
    {
        var index = hash - 1;
        while (index >= 0 && SyntaxFacts.IsWhitespace(text[index]))
        {
            index--;
        }

        return index < 0 || SyntaxFacts.IsNewLine(text[index]);
    }

    private static bool NamesNullable(string text, int index)
    {
        while (index < text.Length && SyntaxFacts.IsWhitespace(text[index]))
        {
            index++;
        }

        return string.CompareOrdinal(text, index, "nullable", 0, "nullable".Length) == 0;
    }
}
