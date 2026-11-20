using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Decides what a split may do with an input that declares a <c>file</c> type. Returns a
/// verdict, never acts on one.
/// </summary>
/// <remarks>
/// <para>
/// A <c>file</c> type is visible only inside the file that declares it, and the compiler
/// builds its metadata name from that file's name. So the type never leaves its file and
/// the file is never renamed. The ordinary types beside it may move out, but only when no
/// moved type can depend on a <c>file</c> type.
/// </para>
/// <para>
/// The proof is syntactic, because the tool builds no semantic model. Leaving the file
/// changes two lookups for a moved type: lookup of a simple name, and lookup of extension
/// members. A name scan covers the first. Nothing syntactic covers the second, so an
/// input whose <c>file</c> type declares extension members is refused outright. A using
/// directive that names a <c>file</c> type is refused too, because every output repeats
/// every using directive. Text a preprocessor directive excludes is refused, because a
/// scan cannot read it. A name match counts even when it looks unrelated: a <c>file</c>
/// type can hide a type of the same name from a namespace or a using directive, and the
/// moved code would bind to that other type with no error. See decision 29.
/// </para>
/// </remarks>
internal static class FileLocalTypes
{
    /// <summary>The input declares a <c>file</c> type and at most one other type, so no type can move.</summary>
    internal const string NothingToSplitReason = "nothing to split: file-local types stay in their file and at most one other type is declared";

    /// <summary>A <c>file</c> type sits in a nested namespace, which the tool does not treat as a type it places.</summary>
    internal const string NestedNamespaceReason = "file-local type sits in a nested namespace; manual split required";

    /// <summary>The input holds text a preprocessor directive excludes, which the name scan cannot read.</summary>
    internal const string ExcludedTextReason = "file-local type shares the file with conditionally excluded text; manual split required";

    /// <summary>A <c>file</c> type declares an extension method or an extension block.</summary>
    internal const string ExtensionMembersReason = "file-local type declares extension members; manual split required";

    /// <summary>A using directive names a <c>file</c> type.</summary>
    internal const string UsingDirectiveReason = "using directive names a file-local type; manual split required";

    /// <summary>A type that would move names a <c>file</c> type.</summary>
    internal const string MovedTypeReason = "moved type names a file-local type; manual split required";

    private const string AttributeSuffix = "Attribute";

    /// <summary>
    /// Refuses an input no choice of kept type could make safe. Returns null for an input
    /// with no <c>file</c> type, and for one that <see cref="DescribeDependency"/> must
    /// still clear.
    /// </summary>
    /// <param name="root">The parsed input.</param>
    /// <param name="types">Every top-level type in the input, in source order.</param>
    /// <returns>The skip reason and a note that names the type involved, or null.</returns>
    internal static (string Reason, string? Note)? DescribeRefusal(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types)
    {
        var declared = FindDeclarations(root).ToArray();
        if (declared.Length == 0)
        {
            return null;
        }

        var fileLocal = types.Where(t => t.HasFileModifier).ToArray();
        if (declared.Length != fileLocal.Length)
        {
            return (NestedNamespaceReason, null);
        }

        if (types.Count - fileLocal.Length < 2)
        {
            return (NothingToSplitReason, null);
        }

        if (root.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            return (ExcludedTextReason, null);
        }

        var extending = fileLocal.FirstOrDefault(DeclaresExtensionMembers);
        if (extending is not null)
        {
            return (ExtensionMembersReason, $"{extending.Key} declares extension members");
        }

        var named = FindUsings(root).Select(directive => FindNamed(directive, fileLocal)).FirstOrDefault(name => name is not null);
        return named is null ? null : (UsingDirectiveReason, $"a using directive names {named}");
    }

    /// <summary>
    /// Picks the type that stays with the <c>file</c> types. The type the keep rule chose
    /// stands whenever the file is named for it. Otherwise the keep rule only took the
    /// first type, and the one ordinary type that names a <c>file</c> type is kept instead.
    /// </summary>
    /// <remarks>
    /// This is safe because a type that names a <c>file</c> type can never move. Keeping it
    /// turns a refusal into a split whose every moved type still passes the same scan. With
    /// two such types one of them would have to move, so the first choice stands and
    /// <see cref="DescribeDependency"/> refuses the input.
    /// </remarks>
    /// <param name="byRule">The type the keep rule chose among the ordinary types.</param>
    /// <param name="namedForFile">True when the file name matches <paramref name="byRule"/>.</param>
    /// <param name="ordinary">The types declared without the <c>file</c> modifier.</param>
    /// <param name="fileLocal">The types declared with it.</param>
    internal static TopLevelType ChooseKept(TopLevelType byRule, bool namedForFile, IReadOnlyList<TopLevelType> ordinary, IReadOnlyList<TopLevelType> fileLocal)
    {
        if (namedForFile)
        {
            return byRule;
        }

        var naming = ordinary.Where(type => FindNamed(type.Node, fileLocal) is not null).Take(2).ToArray();
        return naming.Length == 1 ? naming[0] : byRule;
    }

    /// <summary>
    /// Says which moved type names which <c>file</c> type, or returns null when no moved
    /// type names one. Any identifier counts, including one in a documentation comment and
    /// an attribute written without its <c>Attribute</c> suffix.
    /// </summary>
    /// <param name="moved">The types the plan would move out.</param>
    /// <param name="fileLocal">The types declared with the <c>file</c> modifier.</param>
    internal static string? DescribeDependency(IEnumerable<TopLevelType> moved, IReadOnlyList<TopLevelType> fileLocal)
    {
        foreach (var type in moved)
        {
            if (FindNamed(type.Node, fileLocal) is { } named)
            {
                return $"{type.Key} names {named}";
            }
        }

        return null;
    }

    /// <summary>
    /// The note for a split row whose kept file still holds a <c>file</c> type. The content
    /// phase searches a row's note for collision paths, so this text carries no path.
    /// </summary>
    /// <param name="fileLocal">The types that stay.</param>
    /// <param name="renameWithheld">True when the keep rule would have renamed the file.</param>
    internal static string DescribeStay(IReadOnlyList<TopLevelType> fileLocal, bool renameWithheld)
    {
        var stayed = (fileLocal.Count == 1 ? "file-local type stayed in this file: " : "file-local types stayed in this file: ")
            + string.Join(", ", fileLocal.Select(type => type.Key))
            + "; SA1402 still flags the file";
        return renameWithheld
            ? stayed + "; the file keeps its name because a rename changes a file-local type's metadata name"
            : stayed;
    }

    /// <summary>
    /// The name of a <c>file</c> type that a rendered output names in a using directive or
    /// inside a type, or null. The verifier asks this about every output that leaves the
    /// file, so a planning mistake fails the row before anything is written.
    /// </summary>
    /// <param name="output">The parsed output.</param>
    /// <param name="fileLocal">The <c>file</c> types of the input.</param>
    internal static string? FindNamedInOutput(CompilationUnitSyntax output, IReadOnlyList<TopLevelType> fileLocal)
    {
        return FindUsings(output)
            .Concat<SyntaxNode>(TopLevelType.Find(output).Select(type => type.Node))
            .Select(node => FindNamed(node, fileLocal))
            .FirstOrDefault(name => name is not null);
    }

    /// <summary>
    /// Every type declared with the <c>file</c> modifier, at any namespace depth.
    /// <see cref="TopLevelType.Find"/> stops one namespace down, so it cannot answer this.
    /// </summary>
    internal static IEnumerable<MemberDeclarationSyntax> FindDeclarations(CompilationUnitSyntax root)
    {
        return root.DescendantNodes(IsNamespaceLevel).OfType<MemberDeclarationSyntax>().Where(TopLevelType.IsFileLocal);
    }

    private static IEnumerable<UsingDirectiveSyntax> FindUsings(CompilationUnitSyntax root)
    {
        return root.DescendantNodes(IsNamespaceLevel).OfType<UsingDirectiveSyntax>();
    }

    private static bool IsNamespaceLevel(SyntaxNode node)
    {
        return node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax;
    }

    private static bool DeclaresExtensionMembers(TopLevelType type)
    {
        return type.Node.DescendantNodes().Any(node =>
            node.IsKind(SyntaxKind.ExtensionBlockDeclaration)
            || (node is MethodDeclarationSyntax method
                && method.ParameterList.Parameters.Any(parameter => parameter.Modifiers.Any(SyntaxKind.ThisKeyword))));
    }

    /// <summary>
    /// The first <c>file</c> type an identifier under <paramref name="node"/> names. The
    /// walk enters trivia, which is where a <c>cref</c> lives. It compares the identifier's
    /// value, so a verbatim <c>@Name</c> and a Unicode escape both match.
    /// </summary>
    private static string? FindNamed(SyntaxNode node, IReadOnlyList<TopLevelType> fileLocal)
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
