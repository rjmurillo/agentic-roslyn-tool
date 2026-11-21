using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// The name scan behind <see cref="FileLocalTypes"/>: says whether a piece of syntax names
/// a <c>file</c> type. It reads syntax only and decides nothing about a split.
/// </summary>
/// <remarks>
/// A match is by name alone, on purpose. A <c>file</c> type can hide a type of the same
/// name from a namespace or a using directive, so an identifier that looks unrelated may
/// bind to the <c>file</c> type today and to that other type once the code moves. Every
/// identifier counts, including one in a documentation comment, and an attribute written
/// without its <c>Attribute</c> suffix. See decision 30.
/// </remarks>
internal static class FileLocalNames
{
    private const string AttributeSuffix = "Attribute";

    /// <summary>
    /// The first <c>file</c> type an identifier under <paramref name="node"/> names, or
    /// null. The walk enters trivia, which is where a <c>cref</c> lives. It compares the
    /// identifier's value, so a verbatim <c>@Name</c> and a Unicode escape both match.
    /// </summary>
    /// <param name="node">The syntax to read, for example one type declaration.</param>
    /// <param name="fileLocal">The <c>file</c> types of the input.</param>
    internal static string? FindIn(SyntaxNode node, IReadOnlyList<TopLevelType> fileLocal)
    {
        foreach (var token in node.DescendantTokens(descendIntoTrivia: true))
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken))
            {
                continue;
            }

            var named = fileLocal.FirstOrDefault(type => IsNamedBy(type.Name, token.ValueText));
            if (named is not null)
            {
                return named.Key;
            }
        }

        return null;
    }

    /// <summary>
    /// The first <c>file</c> type a using directive names, or null. Looks at the top of the
    /// file and inside every namespace block, because every output repeats both.
    /// </summary>
    /// <param name="root">The parsed file.</param>
    /// <param name="fileLocal">The <c>file</c> types of the input.</param>
    internal static string? FindInUsings(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> fileLocal)
    {
        return root.DescendantNodes(node => node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Select(directive => FindIn(directive, fileLocal))
            .FirstOrDefault(name => name is not null);
    }

    /// <summary>
    /// The first <c>file</c> type named in a base list of <paramref name="type"/> or of a
    /// type nested in it, or null. A class, struct, or record may implement an interface
    /// whose type arguments hold a <c>file</c> type, and code in another file can then
    /// infer that type without ever writing its name.
    /// </summary>
    /// <param name="type">One type declaration.</param>
    /// <param name="fileLocal">The <c>file</c> types of the input.</param>
    internal static string? FindInBaseLists(SyntaxNode type, IReadOnlyList<TopLevelType> fileLocal)
    {
        return type.DescendantNodesAndSelf()
            .OfType<BaseListSyntax>()
            .Select(list => FindIn(list, fileLocal))
            .FirstOrDefault(name => name is not null);
    }

    /// <summary>
    /// The first <c>file</c> type a rendered output names, in a using directive or inside
    /// a type, or null. The verifier asks this about every output that leaves the file.
    /// </summary>
    /// <param name="output">The parsed output.</param>
    /// <param name="fileLocal">The <c>file</c> types of the input.</param>
    internal static string? FindInOutput(CompilationUnitSyntax output, IReadOnlyList<TopLevelType> fileLocal)
    {
        return FindInUsings(output, fileLocal)
            ?? TopLevelType.Find(output).Select(type => FindIn(type.Node, fileLocal)).FirstOrDefault(name => name is not null);
    }

    private static bool IsNamedBy(string typeName, string identifier)
    {
        if (StringComparer.Ordinal.Equals(identifier, typeName))
        {
            return true;
        }

        return typeName.Length > AttributeSuffix.Length
            && typeName.EndsWith(AttributeSuffix, StringComparison.Ordinal)
            && StringComparer.Ordinal.Equals(identifier, typeName[..^AttributeSuffix.Length]);
    }
}
