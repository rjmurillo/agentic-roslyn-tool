using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Reads which preprocessor symbols a file's conditions name, and which <c>#error</c>
/// directives sit under a branch the build's symbols decide. Parses nothing itself;
/// <see cref="SourceParsing"/> owns the parse rule and uses these answers. Decision 33.
/// </summary>
internal static class PreprocessorSymbols
{
    /// <summary>Checks whether a conditional names a symbol without re-parsing any source.</summary>
    internal static bool HasSymbolConditions(CompilationUnitSyntax root) =>
        root.DescendantTrivia()
            .Select(t => t.GetStructure())
            .Select(d => d switch { IfDirectiveTriviaSyntax i => i.Condition, ElifDirectiveTriviaSyntax e => e.Condition, _ => null })
            .OfType<ExpressionSyntax>()
            .Any(condition => Symbols(condition).Any());

    /// <summary>
    /// The <c>#error</c> directives whose branch depends on a preprocessor symbol the build
    /// sets, at some level of <c>#if</c> nesting. An <c>#elif</c> or <c>#else</c> branch depends on every
    /// condition before it in its group, because it runs only when those are false.
    /// </summary>
    internal static List<ErrorDirectiveTriviaSyntax> SymbolDependentErrorDirectives(CompilationUnitSyntax root)
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
