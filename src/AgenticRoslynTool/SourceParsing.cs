using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AgenticRoslynTool;

/// <summary>
/// The one rule for parsing C#: the options every parse uses, which error diagnostics mean
/// a file is not valid C#, and how to spot a file-based program.
/// </summary>
/// <remarks>
/// <para>
/// A file the C# compiler accepts must never be marked <c>failed</c>. Version 0.2.0 broke
/// that rule twice. It parsed without the <c>FileBasedProgram</c> feature, so the
/// <c>#:</c> and <c>#!</c> lines of a .NET 10 file-based program read as errors. It also
/// treated every <c>#error</c> as a syntax error, although the tool defines no
/// preprocessor symbols, so the <c>#else</c> branch of a target framework check is the
/// active one here and never in the real build.
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

    /// <summary>The compiler's ID for a diagnostic raised by a <c>#error</c> directive.</summary>
    private const string ErrorDirectiveId = "CS1029";

    /// <summary>
    /// The parse options every read of C# uses: the preview language version, and the
    /// <c>FileBasedProgram</c> feature so <c>#:</c> and <c>#!</c> directives parse.
    /// </summary>
    internal static readonly CSharpParseOptions Options = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")]);

    /// <summary>
    /// Returns the error diagnostics that mean <paramref name="tree"/> is not valid C#.
    /// A <c>#error</c> inside an <c>#if</c> group is left out: which branch is active
    /// depends on symbols the real build defines and this tool does not.
    /// </summary>
    internal static Diagnostic[] Errors(SyntaxTree tree)
    {
        var conditionalErrors = ConditionalErrorDirectiveSpans(tree.GetCompilationUnitRoot());
        return tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Where(d => d.Id != ErrorDirectiveId || !conditionalErrors.Any(span => span.IntersectsWith(d.Location.SourceSpan)))
            .ToArray();
    }

    /// <summary>
    /// True when the file is a .NET 10 file-based program, marked by a <c>#:</c> or
    /// <c>#!</c> directive. Such a file compiles on its own, so a type moved to a new file
    /// would drop out of the program.
    /// </summary>
    internal static bool IsFileBasedProgram(CompilationUnitSyntax root) =>
        root.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.IgnoredDirectiveTrivia) || t.IsKind(SyntaxKind.ShebangDirectiveTrivia));

    private static List<TextSpan> ConditionalErrorDirectiveSpans(CompilationUnitSyntax root)
    {
        var spans = new List<TextSpan>();
        var depth = 0;
        foreach (var directive in root.DescendantTrivia().Where(t => t.IsDirective).Select(t => t.GetStructure()).OfType<DirectiveTriviaSyntax>())
        {
            switch (directive.Kind())
            {
                case SyntaxKind.IfDirectiveTrivia:
                    depth++;
                    break;
                case SyntaxKind.EndIfDirectiveTrivia:
                    depth = Math.Max(0, depth - 1);
                    break;
                case SyntaxKind.ErrorDirectiveTrivia when depth > 0:
                    spans.Add(directive.FullSpan);
                    break;
            }
        }

        return spans;
    }
}
