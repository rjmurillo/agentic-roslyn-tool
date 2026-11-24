using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Roslyn syntax rewriter that collapses runs of three or more blank lines down to two
/// while preserving all other trivia. Applied to each output tree after unwanted type
/// declarations are removed, to keep the resulting file tidy without touching semantics.
/// Every top-level type's owned span is left byte for byte: the collapse runs only on
/// text outside those spans.
/// </summary>
/// <remarks>
/// This runs entirely through the Roslyn trivia model, never on the raw file text, and
/// that is deliberate. Blank-looking lines inside a block comment or an <c>#if</c>
/// region live inside a single token's trivia text, so a text-level normalizer would
/// corrupt them. Operating on the trivia list is the single most important reason the
/// splitter is built on the Syntax API.
/// <para>
/// The owned span is the one <see cref="OutputVerifier.GetTypeOwnedText"/> checks: from the
/// type's leading doc comment, or its declaration when it has none, to the end of the
/// declaration. The verifier requires that text to appear unchanged, so shortening a run
/// of blank lines inside a type turned a valid input into a <c>failed</c> row. The
/// rewriter therefore does not descend into a top-level type. Of that type's trivia it
/// collapses only the leading trivia above the owned start, which is where removal of a
/// neighboring type leaves its seam. The types are found with
/// <see cref="TopLevelType.Find"/> and the boundary with
/// <see cref="OutputVerifier.GetTypeOwnedStart"/>, so the protected span and the checked
/// span cannot drift apart.
/// </para>
/// </remarks>
internal sealed class BlankLineCollapser : CSharpSyntaxRewriter
{
    private readonly Dictionary<SyntaxNode, TopLevelType> topLevelTypes;

    private BlankLineCollapser(CompilationUnitSyntax root)
    {
        topLevelTypes = TopLevelType.Find(root).ToDictionary(type => (SyntaxNode)type.Node);
    }

    /// <summary>
    /// Collapses blank line runs in <paramref name="root"/> outside every top-level type's
    /// owned span. The types are found in the same tree that is rewritten, so the node
    /// lookup in <see cref="Visit"/> always matches.
    /// </summary>
    public static CompilationUnitSyntax Collapse(CompilationUnitSyntax root)
    {
        return (CompilationUnitSyntax)new BlankLineCollapser(root).Visit(root)!;
    }

    /// <summary>Leaves a top-level type's owned span untouched and visits every other node as usual.</summary>
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (node is not null && topLevelTypes.TryGetValue(node, out var type))
        {
            return CollapseAboveOwnedSpan(type);
        }

        return base.Visit(node);
    }

    /// <summary>Rewrites both leading and trailing trivia of every token through <see cref="CollapseTriviaList"/>.</summary>
    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        return token
            .WithLeadingTrivia(CollapseTriviaList(token.LeadingTrivia))
            .WithTrailingTrivia(CollapseTriviaList(token.TrailingTrivia));
    }

    private static SyntaxNode CollapseAboveOwnedSpan(TopLevelType type)
    {
        var first = type.Node.GetFirstToken();
        var ownedStart = OutputVerifier.GetTypeOwnedStart(type);
        var above = first.LeadingTrivia.TakeWhile(trivia => trivia.SpanStart < ownedStart).ToList();
        var owned = first.LeadingTrivia.Skip(above.Count);
        var leading = CollapseTriviaList(SyntaxFactory.TriviaList(above)).AddRange(owned);
        return type.Node.ReplaceToken(first, first.WithLeadingTrivia(leading));
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
