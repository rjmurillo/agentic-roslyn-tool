using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Refuses an input that declares a <c>file</c> type when a directive would make the split
/// change what a file means, or break the build around it. Returns a verdict, never acts
/// on one.
/// </summary>
/// <remarks>
/// Both cases break an input with no <c>file</c> type too, and those inputs still split.
/// An input with a <c>file</c> type is refused, because the split must not change meaning.
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
    /// A directive that sets state for the lines below it sits where a split would not
    /// carry it to every output unchanged: on or inside a type, or on an assembly attribute.
    /// The reason names the place, not the loss, because a <c>#define</c> or <c>#undef</c>
    /// on a type is kept and only loses the <c>#if</c> around it.
    /// </summary>
    internal const string DirectiveStateReason = "file-local type shares the file with a #line, #nullable, #pragma, #define, or #undef directive on or inside a type or on an assembly attribute; manual split required";

    /// <summary>
    /// The skip reason for a directive the split cannot carry safely, with a note that
    /// says where it is, or null.
    /// </summary>
    /// <remarks>
    /// A split removes two things from an output: the types it does not hold, and, from
    /// every output but the kept file, the assembly attributes. A directive attached to
    /// either is refused. Most leave with what they are attached to. A <c>#define</c> or
    /// <c>#undef</c> attached to a type is the odd one: removing the type keeps it in every
    /// output, but drops an <c>#if</c> around it, which makes it unconditional. The tool
    /// does not tell those cases apart, and refuses both. A directive attached to a using
    /// directive or to the namespace keyword stays in every output, and is not refused.
    /// </remarks>
    /// <param name="root">The parsed input.</param>
    /// <param name="types">Every top-level type in the input.</param>
    internal static (string Reason, string? Note)? DescribeRefusal(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types)
    {
        if (root.Usings.Any(directive => directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
        {
            return (GlobalUsingReason, null);
        }

        var removable = types.Select(type => type.Node.FullSpan).Concat(root.AttributeLists.Select(list => list.FullSpan)).ToArray();
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (DescribeKind(trivia) is { } kind && removable.Any(span => span.Contains(trivia.SpanStart)))
            {
                var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                return (DirectiveStateReason, $"{kind} directive at line {line}");
            }
        }

        return null;
    }

    /// <summary>
    /// Says whether a rendered output kept every state directive of the input, in order.
    /// The verifier asks this about each output, so a directive the refusal above let a
    /// split drop fails the row before anything is written. It does not see a lost
    /// <c>#if</c> around a directive that stayed. Only the refusal guards that case.
    /// </summary>
    /// <param name="input">The parsed input.</param>
    /// <param name="output">One parsed output.</param>
    internal static bool KeepsEveryDirective(CompilationUnitSyntax input, CompilationUnitSyntax output)
    {
        return ListState(input).SequenceEqual(ListState(output), StringComparer.Ordinal);
    }

    private static IEnumerable<string> ListState(CompilationUnitSyntax root)
    {
        return root.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => DescribeKind(trivia) is not null)
            .Select(trivia => trivia.ToString().Trim());
    }

    /// <summary>The directive's name when it sets state for the lines below it, or null.</summary>
    private static string? DescribeKind(SyntaxTrivia trivia)
    {
        return trivia.Kind() switch
        {
            SyntaxKind.LineDirectiveTrivia or SyntaxKind.LineSpanDirectiveTrivia => "#line",
            SyntaxKind.NullableDirectiveTrivia => "#nullable",
            SyntaxKind.PragmaWarningDirectiveTrivia or SyntaxKind.PragmaChecksumDirectiveTrivia => "#pragma",
            SyntaxKind.DefineDirectiveTrivia => "#define",
            SyntaxKind.UndefDirectiveTrivia => "#undef",
            _ => null,
        };
    }
}
