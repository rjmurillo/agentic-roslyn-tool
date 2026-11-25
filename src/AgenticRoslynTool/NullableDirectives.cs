using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Reads the nullable context that <c>#nullable</c> directives set at a point in a file,
/// restates it in an output file that lost the directives that set it, and checks each
/// output's context before anything is written.
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
/// body would then appear twice, and line conservation rightly refuses that. The fold
/// parses with no symbols defined, so it is exact only for a file with one build; see
/// <see cref="NullableGuard"/>, which refuses the other files before this runs.
/// </remarks>
internal static class NullableDirectives
{
    /// <summary>
    /// Folds every active <c>#nullable</c> directive that starts before
    /// <paramref name="position"/>, in order, from the project default.
    /// </summary>
    /// <remarks>
    /// Reads the tree through <see cref="SourceDirectives.Before"/>, which lists a tree's
    /// directives once, however many types ask about it.
    /// </remarks>
    /// <returns>The annotations and warnings settings in force at the position. Null means the project default.</returns>
    internal static (bool? Annotations, bool? Warnings) StateAt(CSharpSyntaxNode root, int position)
    {
        bool? annotations = null;
        bool? warnings = null;
        foreach (var found in SourceDirectives.Before(root, position))
        {
            if (found is not NullableDirectiveTriviaSyntax { IsActive: true } directive)
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

    /// <summary>The nullable context a type had in its source file, at its first token.</summary>
    internal static (bool? Annotations, bool? Warnings) SourceStateOf(TopLevelType type)
    {
        return StateAt(type.Node.SyntaxTree.GetCompilationUnitRoot(), type.Node.SpanStart);
    }

    /// <summary>Words a context for a message, such as <c>annotations enable, warnings restore</c>.</summary>
    internal static string Describe((bool? Annotations, bool? Warnings) state)
    {
        return $"annotations {Setting(state.Annotations)}, warnings {Setting(state.Warnings)}";
    }

    /// <summary>Reports whether one line is a single <c>#nullable</c> directive and nothing else.</summary>
    internal static bool IsDirectiveLine(string line)
    {
        var trivia = SyntaxFactory.ParseLeadingTrivia(line);
        return trivia.Count == 1 && trivia[0].IsKind(SyntaxKind.NullableDirectiveTrivia);
    }

    /// <summary>
    /// Gives the type a carved output keeps the nullable context it had in the source.
    /// </summary>
    /// <param name="carved">The output tree after the other types were removed.</param>
    /// <param name="kept">The source type the output keeps.</param>
    /// <param name="newLine">The source file's newline, used to end each added line.</param>
    /// <param name="header">The normalized <c>--require-header</c> text, or empty. A banner that opens the output stays above the added lines.</param>
    /// <returns>The output tree, and every line added to it, so the verifier can tell an added line from a duplicated source line.</returns>
    internal static (CompilationUnitSyntax Root, IReadOnlyList<string> AddedLines) Restore(CompilationUnitSyntax carved, TopLevelType kept, string newLine, string header)
    {
        var node = TopLevelType.FindMatch(carved, kept).Node;
        var lines = DirectivesBetween(StateAt(carved, node.SpanStart), SourceStateOf(kept));
        return lines.Count == 0 ? (carved, lines) : (InsertAbove(carved, node, lines, newLine, header), lines);
    }

    /// <summary>
    /// Checks that each output's own type reads the same nullable context in the output body
    /// as it did in the source. The body is parsed again from <see cref="OutputFile.BodyText"/>,
    /// so a misplaced or dropped <c>#nullable</c> line fails the file before it is written.
    /// </summary>
    /// <remarks>
    /// It reads the body, not <see cref="OutputFile.Text"/>, for the reason line conservation
    /// does. A <c>--require-header</c> that holds a <c>#nullable</c> line changes the context
    /// on purpose, because the caller asked for that header. This check uses the same fold as
    /// <see cref="Restore"/>, so it catches a misplaced line, not a wrong
    /// fold. Tests prove the fold against the compiler. It checks the type the output is
    /// named for. A kept file can also hold <c>file</c> types, but
    /// <see cref="FileLocalDirectives"/> refuses any such input with a state directive on or
    /// inside a type, so every type in it shares the prologue's context.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when an output lacks its own type, or when that type's annotations or warnings setting differs from the source.</exception>
    internal static void Verify(string readPath, IReadOnlyList<OutputFile> outputs, CSharpParseOptions parseOptions)
    {
        foreach (var output in outputs)
        {
            var expected = SourceStateOf(output.Type);
            var root = CSharpSyntaxTree.ParseText(output.BodyText, parseOptions, path: output.Path).GetCompilationUnitRoot();
            var written = TopLevelType.FindMatch(root, output.Type);
            var actual = StateAt(root, written.Node.SpanStart);
            if (actual != expected)
            {
                throw new InvalidOperationException($"{readPath}: nullable context for {output.Type.Key} is {Describe(actual)} in {output.Path}, but {Describe(expected)} in the source");
            }
        }
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
    /// One comment is skipped: the required banner, when this type's trivia opens the file
    /// with it. Lines added above it would hide it from <see cref="OutputBuilder.EnsureHeader"/>,
    /// which would then write a second banner.
    /// </remarks>
    private static CompilationUnitSyntax InsertAbove(CompilationUnitSyntax root, MemberDeclarationSyntax node, IReadOnlyList<string> lines, string newLine, string header)
    {
        var leading = node.GetLeadingTrivia();
        var banner = header.Length > 0 && node.FullSpan.Start == 0
            ? OutputBuilder.MatchHeaderEnd(leading.ToFullString(), header, allowLeadingWhitespace: true) ?? 0
            : 0;
        var index = leading.TakeWhile(trivia => trivia.FullSpan.Start < banner || trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia)).Count();

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
