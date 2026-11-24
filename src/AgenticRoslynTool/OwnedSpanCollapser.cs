using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// The blank line collapse that <see cref="OutputBuilder.BuildOutputs"/> runs. It leaves every
/// top-level type's owned span byte for byte and collapses only the text outside those spans.
/// </summary>
/// <remarks>
/// The owned span is the one <see cref="OutputVerifier.GetTypeOwnedText"/> checks: from the
/// type's leading doc comment, or its declaration when it has none, to the end of the
/// declaration. The verifier requires that text to appear unchanged, so shortening a run
/// of blank lines inside a type turned a valid input into a <c>failed</c> row. The
/// rewriter therefore does not descend into a top-level type. Of that type's trivia it
/// collapses only the leading trivia above the owned start, which is where removal of a
/// neighboring type leaves its seam. The types are found with
/// <see cref="TopLevelType.Find"/> and the boundary with
/// <see cref="OutputVerifier.GetTypeOwnedStart"/>, so the protected span and the checked
/// span cannot drift apart. That holds because removal keeps only unbalanced directives, so
/// no doc comment moves from a removed type onto a kept one. Trailing trivia after a type
/// holds at most one line break, so leaving it alone loses no collapse.
/// </remarks>
internal sealed class OwnedSpanCollapser : BlankLineCollapser
{
    private readonly Dictionary<SyntaxNode, TopLevelType> topLevelTypes;

    private OwnedSpanCollapser(CompilationUnitSyntax root)
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
        return (CompilationUnitSyntax)new OwnedSpanCollapser(root).Visit(root)!;
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

    private static SyntaxNode CollapseAboveOwnedSpan(TopLevelType type)
    {
        var first = type.Node.GetFirstToken();
        var ownedStart = OutputVerifier.GetTypeOwnedStart(type);
        var above = first.LeadingTrivia.TakeWhile(trivia => trivia.SpanStart < ownedStart).ToList();
        var owned = first.LeadingTrivia.Skip(above.Count);
        var leading = CollapseTriviaList(new SyntaxTriviaList(above)).AddRange(owned);
        return type.Node.ReplaceToken(first, first.WithLeadingTrivia(leading));
    }
}
