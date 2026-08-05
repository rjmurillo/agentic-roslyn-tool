using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AgenticRoslynTool;

/// <summary>
/// Roslyn syntax rewriter that collapses runs of three or more blank lines down to two
/// while preserving all other trivia. Applied to each output tree after unwanted type
/// declarations are removed, to keep the resulting file tidy without touching semantics.
/// </summary>
/// <remarks>
/// This runs entirely through the Roslyn trivia model, never on the raw file text, and
/// that is deliberate. Blank-looking lines inside a block comment or an <c>#if</c>
/// region live inside a single token's trivia text, so a text-level normalizer would
/// corrupt them. Operating on the trivia list is the single most important reason the
/// splitter is built on the Syntax API.
/// </remarks>
internal sealed class BlankLineCollapser : CSharpSyntaxRewriter
{
    /// <summary>Rewrites both leading and trailing trivia of every token through <see cref="CollapseTriviaList"/>.</summary>
    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        return token
            .WithLeadingTrivia(CollapseTriviaList(token.LeadingTrivia))
            .WithTrailingTrivia(CollapseTriviaList(token.TrailingTrivia));
    }

    private static SyntaxTriviaList CollapseTriviaList(SyntaxTriviaList triviaList)
    {
        var result = new List<SyntaxTrivia>(triviaList.Count);
        var pendingWhitespace = new List<SyntaxTrivia>();
        var endOfLineCount = 0;

        foreach (var trivia in triviaList)
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                endOfLineCount++;
                if (endOfLineCount <= 2)
                {
                    result.AddRange(pendingWhitespace);
                    result.Add(trivia);
                }

                pendingWhitespace.Clear();
                continue;
            }

            if (endOfLineCount > 0 && trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                pendingWhitespace.Add(trivia);
                continue;
            }

            result.AddRange(pendingWhitespace);
            pendingWhitespace.Clear();
            result.Add(trivia);
            endOfLineCount = 0;
        }

        result.AddRange(pendingWhitespace);
        return SyntaxFactory.TriviaList(result);
    }
}

