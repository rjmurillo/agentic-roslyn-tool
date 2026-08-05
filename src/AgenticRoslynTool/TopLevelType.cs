using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Represents one top-level type declaration in a compilation unit. "Top-level" here
/// means the type sits directly in the compilation unit or inside its (single) namespace,
/// which is what SA1402 partitions files on. Also tracks each type's owned leading
/// trivia range, which is how doc comments, attributes, and ordinary comments travel
/// with the type they belong to instead of being stranded when the file is split.
/// </summary>
internal sealed class TopLevelType
{
    /// <summary>Constructs a <see cref="TopLevelType"/> from its syntax node and identifier metadata.</summary>
    /// <param name="node">The Roslyn declaration node for the type.</param>
    /// <param name="name">The simple identifier of the type.</param>
    /// <param name="typeParameters">The type parameter names in declaration order; empty for non-generic types.</param>
    public TopLevelType(MemberDeclarationSyntax node, string name, IReadOnlyList<string> typeParameters)
    {
        Node = node;
        Name = name;
        TypeParameters = typeParameters;
    }

    /// <summary>The Roslyn declaration node backing this type.</summary>
    public MemberDeclarationSyntax Node { get; }

    /// <summary>The simple identifier of the type, without generic arity or arguments.</summary>
    public string Name { get; }

    /// <summary>The declared type parameter names, in source order. Empty for non-generic types.</summary>
    public IReadOnlyList<string> TypeParameters { get; }

    /// <summary>
    /// True when the type is declared with the <c>file</c> modifier. File-local types
    /// are refused because splitting them across files would change their visibility
    /// contract.
    /// </summary>
    public bool HasFileModifier => Node switch { BaseTypeDeclarationSyntax type => type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.FileKeyword)), DelegateDeclarationSyntax type => type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.FileKeyword)), _ => false, };

    /// <summary>
    /// Stable identity for the type across analysis passes. Non-generic types use the
    /// simple name; generic types use <c>Name&lt;T1,T2&gt;</c> so that overloads by
    /// arity can be told apart. Matches the manifest <c>type</c> column.
    /// </summary>
    public string Key => TypeParameters.Count == 0 ? Name : Name + "<" + string.Join(",", TypeParameters) + ">";

    /// <summary>
    /// Enumerates every top-level type declared in the compilation unit, whether declared
    /// at the root or inside the (single) namespace declaration.
    /// </summary>
    /// <param name="root">The compilation unit to walk.</param>
    /// <returns>One <see cref="TopLevelType"/> per class, struct, record, interface, enum, or delegate found.</returns>
    public static IEnumerable<TopLevelType> Find(CompilationUnitSyntax root)
    {
        foreach (var member in root.Members)
        {
            foreach (var type in FromMember(member))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<TopLevelType> FromMember(MemberDeclarationSyntax member)
    {
        if (Create(member) is { } type)
        {
            yield return type;
            yield break;
        }

        if (member is BaseNamespaceDeclarationSyntax namespaceDeclaration)
        {
            foreach (var child in namespaceDeclaration.Members)
            {
                if (Create(child) is { } nestedType)
                {
                    yield return nestedType;
                }
            }
        }
    }

    private static TopLevelType? Create(MemberDeclarationSyntax member)
    {
        return member switch
        {
            TypeDeclarationSyntax type => new TopLevelType(type, type.Identifier.ValueText, type.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToArray() ?? Array.Empty<string>()),
            EnumDeclarationSyntax type => new TopLevelType(type, type.Identifier.ValueText, Array.Empty<string>()),
            DelegateDeclarationSyntax type => new TopLevelType(type, type.Identifier.ValueText, type.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToArray() ?? Array.Empty<string>()),
            _ => null,
        };
    }
}
