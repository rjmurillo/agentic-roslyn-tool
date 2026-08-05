using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AgenticRoslynTool;

/// <summary>
/// One preprocessor directive found in the input, paired with the one-based source line
/// it appears on and the top-level type that owns its position (if any). Feeds the
/// group-owner analysis in <see cref="FileSplitter"/>.
/// </summary>
/// <param name="Trivia">The Roslyn trivia node representing the directive.</param>
/// <param name="Kind">Short label such as <c>#if</c> or <c>#region</c>, extracted from the directive text.</param>
/// <param name="Line">One-based source line number for diagnostic messages.</param>
/// <param name="Owner">The top-level type whose span contains this directive, or null when the directive sits between types.</param>
internal sealed record DirectiveInfo(SyntaxTrivia Trivia, string Kind, int Line, TopLevelType? Owner)
{
    /// <summary>
    /// Builds a <see cref="DirectiveInfo"/> from a Roslyn trivia node, computing the line
    /// number from the syntax tree and extracting the leading directive token from the
    /// trivia's own text.
    /// </summary>
    public static DirectiveInfo Create(SyntaxTree tree, SyntaxTrivia trivia, TopLevelType? owner)
    {
        var line = tree.GetLineSpan(trivia.Span).StartLinePosition.Line + 1;
        return new DirectiveInfo(trivia, trivia.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? trivia.Kind().ToString(), line, owner);
    }
}
