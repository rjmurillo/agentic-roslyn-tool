using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// The one rule for parsing C#: the options every parse uses, which error diagnostics mean
/// a file is not valid C#, how to show them, and how to spot a file-based program.
/// </summary>
/// <remarks>
/// <para>
/// A file the C# compiler accepts must not be marked <c>failed</c>. Version 0.2.0 broke
/// that twice. It parsed without the <c>FileBasedProgram</c> feature, so the <c>#:</c>
/// and <c>#!</c> lines of a .NET 10 file-based program read as errors. It also treated
/// every <c>#error</c> as a syntax error, although the tool defines no preprocessor
/// symbols, so the <c>#else</c> branch of a target framework check is the live one here
/// and never in the real build.
/// </para>
/// <para>
/// The input and every output are judged by this class, so the two can never disagree.
/// <c>ParseRuleOwnershipTests</c> fails the build if a second copy of the rule appears.
/// Decision 33.
/// </para>
/// </remarks>
internal static class SourceParsing
{
    /// <summary>The skip reason for a .NET 10 file-based program.</summary>
    internal const string FileBasedProgramReason = "file-based program (#: or #! directive): it compiles alone, so no type can move out of it";

    /// <summary>The skip reason for a file that parses only once its preprocessor symbols are defined.</summary>
    internal const string SymbolDependentReason = "parses only with the preprocessor symbols its #if lines name, which this tool does not define; manual split required";

    /// <summary>
    /// The parse options every read of C# uses: the preview language version, and the
    /// <c>FileBasedProgram</c> feature so <c>#:</c> and <c>#!</c> directives parse.
    /// </summary>
    internal static readonly CSharpParseOptions Options = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")]);

    /// <summary>
    /// Returns the error diagnostics that mean <paramref name="tree"/> is not valid C#.
    /// </summary>
    /// <remarks>
    /// A diagnostic raised by an <c>#error</c> directive is left out when the branch that
    /// holds the directive depends on a preprocessor symbol. The real build defines symbols
    /// this tool does not, so it may never take that branch. Matching the diagnostic by
    /// location covers every ID such a directive raises: CS1029 for <c>#error text</c> and
    /// CS8304 for <c>#error version</c>. A branch whose conditions use no symbol, such as
    /// <c>#if true</c>, is live in every build, so its <c>#error</c> still counts, as does
    /// one outside any <c>#if</c>.
    /// </remarks>
    internal static Diagnostic[] Errors(SyntaxTree tree)
    {
        var excused = SymbolDependentErrorDirectives(tree.GetCompilationUnitRoot());
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => !excused.Any(directive => directive.FullSpan.IntersectsWith(d.Location.SourceSpan)))
            .ToArray();
    }

    /// <summary>
    /// True when <paramref name="tree"/> has errors with no symbols defined, but none once
    /// every symbol its <c>#if</c> and <c>#elif</c> conditions name is defined. With no
    /// symbols, a group of branches that all name symbols leaves no branch live, and the
    /// code around it can stop parsing. The real build defines one of them. The tool cannot
    /// split such a file safely, because it would split a tree the build never sees, so the
    /// caller skips it rather than failing it.
    /// </summary>
    internal static bool ParsesWithItsSymbols(SyntaxTree tree)
    {
        var symbols = tree.GetCompilationUnitRoot().DescendantTrivia()
            .Select(t => t.GetStructure())
            .Select(s => s switch { IfDirectiveTriviaSyntax i => i.Condition, ElifDirectiveTriviaSyntax e => e.Condition, _ => null })
            .OfType<ExpressionSyntax>()
            .SelectMany(c => c.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            .Select(n => n.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (symbols.Length == 0)
        {
            return false;
        }

        var withSymbols = CSharpSyntaxTree.ParseText(tree.GetText(), SourceParsing.Options.WithPreprocessorSymbols(symbols), path: tree.FilePath);
        return Errors(withSymbols).Length == 0;
    }

    /// <summary>Renders diagnostics as one semicolon-separated string for a row's reason.</summary>
    internal static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.GetMessage()}"));

    /// <summary>
    /// True when the file is a .NET 10 file-based program, marked by a <c>#:</c> or
    /// <c>#!</c> directive. Such a file compiles on its own, so a type moved to a new file
    /// would drop out of the program.
    /// </summary>
    internal static bool IsFileBasedProgram(CompilationUnitSyntax root) =>
        root.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.IgnoredDirectiveTrivia) || t.IsKind(SyntaxKind.ShebangDirectiveTrivia));

    /// <summary>
    /// The <c>#error</c> directives whose branch depends on a preprocessor symbol at some
    /// level of <c>#if</c> nesting. An <c>#elif</c> or <c>#else</c> branch depends on every
    /// condition before it in its group, because it runs only when those are false.
    /// </summary>
    private static List<ErrorDirectiveTriviaSyntax> SymbolDependentErrorDirectives(CompilationUnitSyntax root)
    {
        var excused = new List<ErrorDirectiveTriviaSyntax>();
        // One entry per open #if group: whether any condition seen so far in it names a symbol.
        var groups = new Stack<bool>();
        foreach (var directive in root.DescendantTrivia().Where(t => t.IsDirective).Select(t => t.GetStructure()).OfType<DirectiveTriviaSyntax>())
        {
            switch (directive)
            {
                case IfDirectiveTriviaSyntax ifDirective:
                    groups.Push(NamesSymbol(ifDirective.Condition));
                    break;
                case ElifDirectiveTriviaSyntax elif when groups.Count > 0:
                    groups.Push(groups.Pop() || NamesSymbol(elif.Condition));
                    break;
                case EndIfDirectiveTriviaSyntax when groups.Count > 0:
                    groups.Pop();
                    break;
                case ErrorDirectiveTriviaSyntax error when groups.Any(dependsOnSymbol => dependsOnSymbol):
                    excused.Add(error);
                    break;
            }
        }

        return excused;
    }

    private static bool NamesSymbol(ExpressionSyntax condition) =>
        condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any();
}
