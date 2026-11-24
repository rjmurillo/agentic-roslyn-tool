using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// The positions in a type where the oracle reads the nullable context: its first token, and
/// the first token of each member and each <c>?</c> annotation inside it, in order.
/// </summary>
/// <remarks>
/// A directive inside a type body changes context only for what follows it, so the type's
/// first token alone would miss it. Review found a wrong context that only a member showed.
/// </remarks>
internal static class NullableCheckPoints
{
    /// <summary>Every position to check in <paramref name="type"/>, in document order.</summary>
    internal static IEnumerable<int> Of(TopLevelType type)
    {
        return type.Node.DescendantNodesAndSelf()
            .Where(node => node is MemberDeclarationSyntax or NullableTypeSyntax)
            .Select(node => node.SpanStart);
    }
}
