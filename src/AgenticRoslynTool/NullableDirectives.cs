using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Reads the nullable context that <c>#nullable</c> directives set at a point in a file, and
/// restates it in an output file that lost the directives that set it.
/// </summary>
/// <remarks>
/// A <c>#nullable</c> directive sits in the leading trivia of the type below it, or inside a
/// type's body. A split removes every other type with its trivia, so a type that followed
/// the directive in the source can land in a file that never sets the context. Its
/// <c>string?</c> then raises CS8632, and its warnings change. This type carries the context
/// across without copying any source line: it folds the directives in order, compares the
/// carved output with the source, and adds the fewest canonical lines that close the gap.
/// A context is two settings, annotations and warnings. Each is on, off, or the project
/// default, which <c>#nullable restore</c> returns to. Null stands for the project default.
/// Copying the source directives instead does not work. A directive inside a removed type's
/// body would then appear twice, and line conservation rightly refuses that.
/// </remarks>
internal static class NullableDirectives
{
    /// <summary>
    /// Folds every active <c>#nullable</c> directive that starts before
    /// <paramref name="position"/>, in order, from the project default.
    /// </summary>
    /// <returns>The annotations and warnings settings in force at the position. Null means the project default.</returns>
    internal static (bool? Annotations, bool? Warnings) StateAt(SyntaxNode root, int position)
    {
        bool? annotations = null;
        bool? warnings = null;
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.SpanStart >= position)
            {
                break;
            }

            if (trivia.GetStructure() is not NullableDirectiveTriviaSyntax { IsActive: true } directive)
            {
                continue;
            }

            bool? value = directive.SettingToken.Kind() switch
            {
                SyntaxKind.EnableKeyword => true,
                SyntaxKind.DisableKeyword => false,
                _ => null,
            };
            var target = directive.TargetToken.Kind();
            if (target != SyntaxKind.WarningsKeyword)
            {
                annotations = value;
            }

            if (target != SyntaxKind.AnnotationsKeyword)
            {
                warnings = value;
            }
        }

        return (annotations, warnings);
    }

    /// <summary>
    /// The directive lines that move a file from one nullable context to another. One line
    /// when both settings end equal, otherwise one line per setting that changes, and none
    /// when the contexts already match.
    /// </summary>
    internal static IReadOnlyList<string> DirectivesBetween((bool? Annotations, bool? Warnings) from, (bool? Annotations, bool? Warnings) to)
    {
        if (from == to)
        {
            return Array.Empty<string>();
        }

        if (to.Annotations == to.Warnings)
        {
            return new[] { "#nullable " + Setting(to.Annotations) };
        }

        var lines = new List<string>();
        if (from.Annotations != to.Annotations)
        {
            lines.Add("#nullable " + Setting(to.Annotations) + " annotations");
        }

        if (from.Warnings != to.Warnings)
        {
            lines.Add("#nullable " + Setting(to.Warnings) + " warnings");
        }

        return lines;
    }

    /// <summary>
    /// Gives each type left in a carved output the nullable context it had in the source.
    /// </summary>
    /// <param name="carved">The output tree after the other types were removed.</param>
    /// <param name="kept">The source types the output keeps, in source order.</param>
    /// <param name="newLine">The source file's newline, used to end each added line.</param>
    /// <returns>The output tree, and every line added to it, so the verifier can tell an added line from a duplicated source line.</returns>
    internal static (CompilationUnitSyntax Root, IReadOnlyList<string> AddedLines) Restore(CompilationUnitSyntax carved, IReadOnlyList<TopLevelType> kept, string newLine)
    {
        var added = new List<string>();
        for (var i = 0; i < kept.Count; i++)
        {
            var remaining = TopLevelType.Find(carved).ToArray();
            if (remaining.Length != kept.Count)
            {
                throw new InvalidOperationException($"output keeps {remaining.Length} top-level types, expected {kept.Count}");
            }

            var node = remaining[i].Node;
            var want = StateAt(kept[i].Node.SyntaxTree.GetRoot(), kept[i].Node.SpanStart);
            var lines = DirectivesBetween(StateAt(carved, node.SpanStart), want);
            if (lines.Count == 0)
            {
                continue;
            }

            carved = InsertAbove(carved, node, lines, newLine);
            added.AddRange(lines);
        }

        return (carved, added);
    }

    /// <summary>
    /// Inserts directive lines at the start of the line that holds a type's first comment,
    /// attribute, or keyword. That keeps a doc comment next to its type, so the owned text
    /// the verifier counts is unchanged. A directive already in the type's leading trivia
    /// stays below the added lines and still wins, and it only sets a setting the source
    /// and the output already agree on.
    /// </summary>
    /// <remarks>
    /// The lines go in at the start of a line. A type that shares its first line with an
    /// earlier token never reaches here in a split that passes: line conservation already
    /// refuses that file. If one did, the added directive would sit mid-line, and the parse
    /// check in <see cref="OutputVerifier.VerifyOutputs"/> would fail the file unwritten.
    /// </remarks>
    private static CompilationUnitSyntax InsertAbove(CompilationUnitSyntax root, MemberDeclarationSyntax node, IReadOnlyList<string> lines, string newLine)
    {
        var leading = node.GetLeadingTrivia();
        var index = 0;
        while (index < leading.Count && (leading[index].IsKind(SyntaxKind.WhitespaceTrivia) || leading[index].IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            index++;
        }

        while (index > 0 && leading[index - 1].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            index--;
        }

        var directives = string.Concat(lines.Select(line => line + newLine));
        var updated = leading.InsertRange(index, SyntaxFactory.ParseLeadingTrivia(directives));
        return root.ReplaceNode(node, node.WithLeadingTrivia(updated));
    }

    private static string Setting(bool? value)
    {
        return value switch
        {
            true => "enable",
            false => "disable",
            null => "restore",
        };
    }
}
