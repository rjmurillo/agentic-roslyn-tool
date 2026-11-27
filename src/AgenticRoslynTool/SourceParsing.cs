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

    /// <summary>The skip reason for syntax errors that may depend on build symbols.</summary>
    internal const string SymbolDependentReason = "input has syntax errors under default preprocessor symbols; build symbols may change the parse; manual review required";

    /// <summary>The diagnostic IDs an <c>#error</c> directive raises.</summary>
    private static readonly HashSet<string> ErrorDirectiveIds = new(StringComparer.Ordinal) { "CS1029", "CS8304" };

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
    /// holds the directive depends on a preprocessor symbol the build sets. The real build
    /// defines symbols this tool does not, so it may never take that branch. A diagnostic is
    /// excused only when it carries an ID an <c>#error</c> raises, CS1029 for
    /// <c>#error text</c> and CS8304 for <c>#error version</c>, and sits on that directive.
    /// A broken neighbor, such as a second <c>#else</c>, is never excused. A branch whose
    /// conditions name no symbol, or only symbols the file sets itself with <c>#define</c>
    /// or <c>#undef</c> outside any symbol-dependent branch, has the same value in every
    /// build, so its <c>#error</c> still counts, as does one outside any <c>#if</c>. A
    /// symbol a symbol-dependent branch sets or unsets counts as a build symbol from there
    /// on, until an unconditional <c>#define</c> or <c>#undef</c> later in the file fixes it.
    /// </remarks>
    internal static Diagnostic[] Errors(SyntaxTree tree)
    {
        var excused = SymbolDependentErrorDirectives(tree.GetCompilationUnitRoot());
        // The excused directives come in document order, so each diagnostic needs one binary
        // search. Scanning them all per diagnostic, or calling FindTrivia, was quadratic: a
        // file with 80,000 guarded #error lines took minutes. Decision 33 has the numbers.
        var starts = excused.Select(e => e.Span.Start).ToArray();
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => !(ErrorDirectiveIds.Contains(d.Id) && SitsOnExcused(d.Location.SourceSpan.Start, starts, excused)))
            .ToArray();
    }

    /// <summary>Checks whether a conditional names a symbol without re-parsing any source.</summary>
    internal static bool HasSymbolConditions(CompilationUnitSyntax root) =>
        root.DescendantTrivia()
            .Select(t => t.GetStructure())
            .Select(d => d switch { IfDirectiveTriviaSyntax i => i.Condition, ElifDirectiveTriviaSyntax e => e.Condition, _ => null })
            .OfType<ExpressionSyntax>()
            .Any(condition => Symbols(condition).Any());

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
    /// The <c>#error</c> directives whose branch depends on a preprocessor symbol the build
    /// sets, at some level of <c>#if</c> nesting. An <c>#elif</c> or <c>#else</c> branch depends on every
    /// condition before it in its group, because it runs only when those are false.
    /// </summary>
    private static List<ErrorDirectiveTriviaSyntax> SymbolDependentErrorDirectives(CompilationUnitSyntax root)
    {
        var excused = new List<ErrorDirectiveTriviaSyntax>();
        // Symbols the file sets itself, outside any branch that depends on a build symbol.
        // Only those have the same value in every build. A #define under #if !NET removes its
        // symbol from this set until an unconditional #define or #undef puts it back.
        var setByFile = new HashSet<string>(StringComparer.Ordinal);
        // One entry per open #if group: whether any condition seen so far in it names a build
        // symbol. dependentGroups counts the true entries, so each check is constant time.
        var groups = new Stack<bool>();
        var dependentGroups = 0;
        foreach (var directive in root.DescendantTrivia().Where(t => t.IsDirective).Select(t => t.GetStructure()).OfType<DirectiveTriviaSyntax>())
        {
            switch (directive)
            {
                // Under a symbol-dependent branch a directive runs in some builds, whether or
                // not it is live here, so an inactive one counts too. Outside such a branch an
                // inactive directive sits under a constant false condition and never runs.
                case DefineDirectiveTriviaSyntax define when define.IsActive || dependentGroups > 0:
                    RecordSet(define.Name.ValueText, dependentGroups > 0, setByFile);
                    break;
                case UndefDirectiveTriviaSyntax undef when undef.IsActive || dependentGroups > 0:
                    RecordSet(undef.Name.ValueText, dependentGroups > 0, setByFile);
                    break;
                case IfDirectiveTriviaSyntax ifDirective:
                    var ifDepends = NamesBuildSymbol(ifDirective.Condition, setByFile);
                    groups.Push(ifDepends);
                    dependentGroups += ifDepends ? 1 : 0;
                    break;
                case ElifDirectiveTriviaSyntax elif when groups.Count > 0:
                    var before = groups.Pop();
                    var after = before || NamesBuildSymbol(elif.Condition, setByFile);
                    groups.Push(after);
                    dependentGroups += after && !before ? 1 : 0;
                    break;
                case EndIfDirectiveTriviaSyntax when groups.Count > 0:
                    dependentGroups -= groups.Pop() ? 1 : 0;
                    break;
                case ErrorDirectiveTriviaSyntax error when dependentGroups > 0:
                    excused.Add(error);
                    break;
            }
        }

        return excused;
    }

    private static bool SitsOnExcused(int position, int[] starts, List<ErrorDirectiveTriviaSyntax> excused)
    {
        var index = Array.BinarySearch(starts, position);
        index = index >= 0 ? index : ~index - 1;
        return index >= 0 && position < excused[index].Span.End;
    }

    private static void RecordSet(string symbol, bool underBuildSymbol, HashSet<string> setByFile)
    {
        if (underBuildSymbol)
        {
            setByFile.Remove(symbol);
        }
        else
        {
            // An unconditional #define or #undef fixes the symbol for every build from here on,
            // whatever a symbol-dependent branch did to it earlier.
            setByFile.Add(symbol);
        }
    }

    private static bool NamesBuildSymbol(ExpressionSyntax condition, HashSet<string> setByFile) =>
        Symbols(condition).Any(n => !setByFile.Contains(n.Identifier.ValueText));

    // The symbols a condition names. Error recovery invents a missing identifier for
    // `#if` with no condition or `#if 1`; that names nothing, so it is left out.
    private static IEnumerable<IdentifierNameSyntax> Symbols(ExpressionSyntax condition) =>
        condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Where(n => !n.Identifier.IsMissing);
}
