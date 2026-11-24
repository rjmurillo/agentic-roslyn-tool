using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Lists the preprocessor directives of a syntax tree, active or not, in document order.
/// </summary>
/// <remarks>
/// A split asks about the same source tree once for each type it moves. Walking that tree
/// each time made a file with many types take time that grew with the square of its type
/// count. Before this cache one generated file of 1800 types with a <c>#pragma</c> each
/// planned in 54.5s against 6.1s on main. So each
/// tree's directives are listed once and kept, keyed weakly on the root, which lets a tree
/// and its list be collected together. See decision 31.
/// </remarks>
internal static class SourceDirectives
{
    private static readonly ConditionalWeakTable<CSharpSyntaxNode, DirectiveTriviaSyntax[]> Listed = new();

    /// <summary>Every directive in <paramref name="root"/> that starts before <paramref name="position"/>.</summary>
    internal static IEnumerable<DirectiveTriviaSyntax> Before(CSharpSyntaxNode root, int position)
    {
        return Listed.GetValue(root, List).TakeWhile(directive => directive.SpanStart < position);
    }

    private static DirectiveTriviaSyntax[] List(CSharpSyntaxNode root)
    {
        var directives = new List<DirectiveTriviaSyntax>();
        for (var directive = root.GetFirstDirective(); directive is not null; directive = directive.GetNextDirective())
        {
            directives.Add(directive);
        }

        return directives.ToArray();
    }
}
