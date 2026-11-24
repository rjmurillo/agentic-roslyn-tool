using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Decides whether some type's nullable context could differ between the source and a new
/// file in some build. Returns a verdict, never acts on one.
/// </summary>
/// <remarks>
/// <see cref="NullableDirectives.StateAt"/> folds the parse with no symbols defined, and
/// <see cref="NullableDirectives.Restore"/> restates what it finds as unconditional lines.
/// That is exact only when every build reads the same <c>#nullable</c> directives before each
/// type in the source and in its new file. Two things break that, and this refuses both.
/// <list type="number">
/// <item>A <c>#nullable</c> under <c>#if</c> that starts before the last type. Then some type's
/// context in the source depends on the build. Comparing what each output loses is not
/// enough: a conditional directive in a type's own leading trivia travels with it, so no
/// output loses it, yet an unconditional directive above it can be lost. Review found that
/// wrong write after a counting rule had passed five rounds.</item>
/// <item>Any <c>#define</c> or <c>#undef</c>, in a file that also holds a <c>#nullable</c> under
/// <c>#if</c>. An <c>#if</c> condition reads only project symbols and the file's own
/// definitions, and a split can change the definitions a new file sees. It drops the
/// <c>#if</c> around a definition above the first type, and it drops a definition above an
/// <c>[assembly: ...]</c> attribute, which only the kept file keeps. A conditional
/// <c>#nullable</c> that reads the symbol, even inside the last type, then sets a different
/// context. Review found the two drops in turn, so the rule refuses every definition rather
/// than the places one can be lost. With no definition, every file sees the same symbols.</item>
/// </list>
/// <para>
/// Earlier checks run first and refuse some of these files with their own reasons. A
/// conditional <c>#nullable</c> inside the last type, after its start, is allowed when no
/// definition is conditional: it sets context only for that type's members and travels with
/// it. The refusal does not read conditions, so it also refuses <c>#if false</c>.
/// </para>
/// See decision 31.
/// </remarks>
internal static class ConditionalNullable
{
    /// <summary>
    /// Reports whether a <c>#nullable</c> under <c>#if</c> starts before the last type in
    /// <paramref name="types"/>, or whether the file holds both a <c>#nullable</c> under
    /// <c>#if</c> and any <c>#define</c> or <c>#undef</c>.
    /// </summary>
    internal static bool AffectsAType(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types)
    {
        if (CountBefore(root, types.Max(type => type.Node.SpanStart)) > 0)
        {
            return true;
        }

        var end = root.FullSpan.End;
        return CountBefore(root, end) > 0
            && SourceDirectives.Before(root, end).Any(directive => directive.Kind() is SyntaxKind.DefineDirectiveTrivia or SyntaxKind.UndefDirectiveTrivia);
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
