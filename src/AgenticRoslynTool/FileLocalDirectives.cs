using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Refuses an input that declares a <c>file</c> type when a directive would make the split
/// change what the kept file means, or break the build around it. Returns a verdict, never
/// acts on one.
/// </summary>
/// <remarks>
/// Both cases break an input with no <c>file</c> type too, and those inputs still split.
/// An input with a <c>file</c> type is refused, because its file must not change meaning.
/// See decision 30.
/// </remarks>
internal static class FileLocalDirectives
{
    /// <summary>
    /// The input holds a <c>global using</c> directive. Every output repeats it, and a
    /// global alias declared in two files does not compile.
    /// </summary>
    internal const string GlobalUsingReason = "file-local type shares the file with a global using directive; manual split required";

    /// <summary>
    /// A <c>#line</c>, <c>#nullable</c>, or <c>#pragma</c> directive sits on or inside a
    /// type. It applies to everything below it, but a split carries it to one output only.
    /// </summary>
    internal const string DirectiveStateReason = "file-local type shares the file with a #line, #nullable, or #pragma directive that only one output would keep; manual split required";

    /// <summary>
    /// The skip reason for a directive the split cannot carry safely, with a note that
    /// says where it is, or null. A directive above the namespace belongs to no type, so
    /// every output repeats it and it is not refused.
    /// </summary>
    /// <param name="root">The parsed input.</param>
    /// <param name="types">Every top-level type in the input.</param>
    internal static (string Reason, string? Note)? DescribeRefusal(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types)
    {
        if (root.Usings.Any(directive => directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
        {
            return (GlobalUsingReason, null);
        }

        // A directive that sets state for the lines below it travels with the one type it
        // is attached to. The kept file would lose it, or a moved file would.
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (IsStateDirective(trivia) && types.Any(type => type.Node.FullSpan.Contains(trivia.SpanStart)))
            {
                var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                return (DirectiveStateReason, $"{trivia.ToString().Split(' ', 2)[0].Trim()} directive at line {line}");
            }
        }

        return null;
    }

    private static bool IsStateDirective(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.LineDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.LineSpanDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.NullableDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.PragmaWarningDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.PragmaChecksumDirectiveTrivia);
    }
}
