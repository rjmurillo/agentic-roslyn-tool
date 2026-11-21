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
/// The check is syntactic, because the tool builds no semantic model. A moved type was
/// found to depend on a <c>file</c> type in three ways: by its name, by an extension
/// member, and by inference from a base list. The name scan in
/// <see cref="FileLocalNames"/> covers the first. Nothing syntactic covers the second,
/// so an input whose <c>file</c> type declares extension members is refused outright. A
/// base list that names a <c>file</c> type is refused for the third. A using directive
/// that names a <c>file</c> type is refused too, because every output repeats every using
/// directive. Text a preprocessor directive excludes is refused, because a scan cannot
/// read it. <see cref="FileLocalDirectives"/> adds two refusals that guard the kept file
/// itself. See decision 30.
/// </para>
/// <para>
/// Call <see cref="DescribeRefusal"/> first and <see cref="PlanAround"/> second. The
/// first refuses the inputs no choice of kept type could make safe. The second assumes
/// those are gone. <see cref="DescribeViolation"/> asks again about the rendered text.
/// </para>
/// </remarks>
internal static class FileLocalTypes
{
    /// <summary>The input declares a <c>file</c> type and at most one other type, so no type can move.</summary>
    internal const string NothingToSplitReason = "nothing to split: file-local types stay in their file and at most one other type is declared";

    /// <summary>The input holds text a preprocessor directive excludes, which the name scan cannot read.</summary>
    internal const string ExcludedTextReason = "file-local type shares the file with conditionally excluded text; manual split required";

    /// <summary>A <c>file</c> type declares an extension method or an extension block.</summary>
    internal const string ExtensionMembersReason = "file-local type declares extension members; manual split required";

    /// <summary>A using directive names a <c>file</c> type.</summary>
    internal const string UsingDirectiveReason = "using directive names a file-local type; manual split required";

    /// <summary>
    /// An ordinary type lists a <c>file</c> type among its base types, for example as a
    /// type argument of an interface it implements.
    /// </summary>
    internal const string BaseListReason = "type lists a file-local type among its base types; manual split required";

    /// <summary>A type that would move names a <c>file</c> type.</summary>
    internal const string MovedTypeReason = "moved type names a file-local type; manual split required";

    /// <summary>
    /// How the note on a split row starts when a <c>file</c> type stayed. Published: the
    /// packaged agent documents tell a caller to match on it, and
    /// <c>PackagedAgentDocsTests</c> holds them to this text.
    /// </summary>
    internal const string StayNotePrefix = "file-local type";

    /// <summary>
    /// Refuses an input no choice of kept type could make safe. Returns null for an input
    /// with no <c>file</c> type, and for one that <see cref="PlanAround"/> must still
    /// clear.
    /// </summary>
    /// <param name="types">Every top-level type in the input, in source order.</param>
    /// <returns>The skip reason and a note that names the type involved, or null.</returns>
    internal static (string Reason, string? Note)? DescribeRefusal(IReadOnlyList<TopLevelType> types)
    {
        var fileLocal = types.Where(t => t.HasFileModifier).ToArray();
        if (fileLocal.Length == 0)
        {
            return null;
        }

        var root = fileLocal[0].Node.SyntaxTree.GetCompilationUnitRoot();

        if (types.Count - fileLocal.Length < 2)
        {
            return (NothingToSplitReason, null);
        }

        if (root.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia)))
        {
            return (ExcludedTextReason, null);
        }

        if (FileLocalDirectives.DescribeRefusal(root, types) is { } directive)
        {
            return directive;
        }

        var extending = fileLocal.FirstOrDefault(DeclaresExtensionMembers);
        if (extending is not null)
        {
            return (ExtensionMembersReason, $"{extending.Key} declares extension members");
        }

        if (FileLocalNames.FindInUsings(root, fileLocal) is { } named)
        {
            return (UsingDirectiveReason, $"a using directive names {named}");
        }

        // The type that stays may name a file-local type, but not in a base list. From
        // there the type reaches other files with no name at the use site: a call that
        // takes the interface infers it.
        foreach (var type in types.Where(t => !t.HasFileModifier))
        {
            if (FileLocalNames.FindInBaseLists(type.Node, fileLocal) is { } listed)
            {
                return (BaseListReason, $"{type.Key} lists {listed} among its base types");
            }
        }

        return null;
    }

    /// <summary>
    /// Plans around the <c>file</c> types of an input <see cref="DescribeRefusal"/> let
    /// through: picks the type that stays with them, then either refuses the input because
    /// a type that would move names a <c>file</c> type, or lets the split go ahead.
    /// </summary>
    /// <remarks>
    /// The type the keep rule chose stands whenever the rule chose it for its name.
    /// Otherwise the keep rule only took the first type, and the one ordinary type that
    /// names a <c>file</c> type is kept instead. That is safe because such a type can never move: keeping it turns a
    /// refusal into a split whose every moved type still passes the same scan. With two
    /// such types one of them would have to move, so the first choice stands and the
    /// input is refused.
    /// </remarks>
    /// <param name="byRule">The type the keep rule chose among the ordinary types.</param>
    /// <param name="namedForFile">True when the keep rule chose <paramref name="byRule"/> for its name, or the file already has that type's name. False when the rule only took the first type.</param>
    /// <param name="ordinary">The types declared without the <c>file</c> modifier.</param>
    /// <param name="fileLocal">The types declared with it.</param>
    /// <returns>
    /// The kept type, then null, or the note for a row skipped with
    /// <see cref="MovedTypeReason"/>.
    /// </returns>
    internal static (TopLevelType Keep, string? RefusalNote) PlanAround(TopLevelType byRule, bool namedForFile, IReadOnlyList<TopLevelType> ordinary, IReadOnlyList<TopLevelType> fileLocal)
    {
        var keep = namedForFile ? byRule : ChooseNaming(byRule, ordinary, fileLocal);
        foreach (var type in ordinary.Where(t => !ReferenceEquals(t, keep)))
        {
            if (FileLocalNames.FindIn(type.Node, fileLocal) is { } named)
            {
                return (keep, $"{type.Key} names {named}");
            }
        }

        return (keep, null);
    }

    /// <summary>
    /// Checks the rendered outputs of an input that declares a <c>file</c> type, and says
    /// what is wrong or returns null. One output must be written to the path the input was
    /// read from, or the file would be renamed. Every other output must declare no
    /// <c>file</c> type and name none. Every output must keep every directive that sets
    /// state for the lines below it.
    /// </summary>
    /// <remarks>
    /// <see cref="PlanAround"/> already refuses these inputs. This asks again about the
    /// text that would reach disk, so a planning mistake fails the row before any write.
    /// </remarks>
    /// <param name="readPath">The path the input was read from.</param>
    /// <param name="inputTypes">Every top-level type in the input.</param>
    /// <param name="outputs">The rendered outputs.</param>
    /// <param name="parseOptions">The options the input was parsed with.</param>
    internal static string? DescribeViolation(string readPath, IReadOnlyList<TopLevelType> inputTypes, IReadOnlyList<OutputFile> outputs, CSharpParseOptions parseOptions)
    {
        var fileLocal = inputTypes.Where(t => t.HasFileModifier).ToArray();
        if (fileLocal.Length == 0)
        {
            return null;
        }

        if (!outputs.Any(o => PathComparison.Comparer.Equals(o.Path, readPath)))
        {
            return $"{readPath}: a file that declares a file-local type would be renamed";
        }

        var input = fileLocal[0].Node.SyntaxTree.GetCompilationUnitRoot();
        foreach (var output in outputs)
        {
            var root = CSharpSyntaxTree.ParseText(output.Text, parseOptions, path: output.Path).GetCompilationUnitRoot();
            if (!FileLocalDirectives.KeepsEveryDirective(input, root))
            {
                return $"{output.Path} would not keep every #line, #nullable, #pragma, #define, and #undef directive of {readPath}";
            }

            if (PathComparison.Comparer.Equals(output.Path, readPath))
            {
                continue;
            }

            if (TopLevelType.Find(root).Any(t => t.HasFileModifier))
            {
                return $"{output.Path}: a file-local type would leave {readPath}";
            }

            if (FileLocalNames.FindInOutput(root, fileLocal) is { } named)
            {
                return $"{output.Path} names file-local type {named}, which stays in {readPath}";
            }
        }

        return null;
    }

    /// <summary>
    /// The one ordinary type that names a <c>file</c> type, or <paramref name="byRule"/>
    /// when none does or more than one does.
    /// </summary>
    private static TopLevelType ChooseNaming(TopLevelType byRule, IReadOnlyList<TopLevelType> ordinary, IReadOnlyList<TopLevelType> fileLocal)
    {
        var naming = ordinary.Where(type => FileLocalNames.FindIn(type.Node, fileLocal) is not null).Take(2).ToArray();
        return naming.Length == 1 ? naming[0] : byRule;
    }

    /// <summary>
    /// The note for a split row whose kept file still holds a <c>file</c> type. The content
    /// phase searches a row's note for collision paths, so this text carries no path.
    /// </summary>
    /// <remarks>
    /// With its default settings StyleCop SA1402 checks classes and records only. It was
    /// measured to flag a file that holds two of them, and to stay quiet when the
    /// <c>file</c> types beside one class were structs, interfaces, enums, or delegates.
    /// Every note says SA1402 still flags the file. When fewer than two classes or records
    /// stay, it adds where: only a build set to check more than classes raises the warning.
    /// </remarks>
    /// <param name="keep">The ordinary type that stays with the <c>file</c> types.</param>
    /// <param name="fileLocal">The <c>file</c> types of the input.</param>
    /// <param name="renameWithheld">True when the file does not have the name the tool would give the kept type's file. The note then says the file keeps its name.</param>
    internal static string DescribeStay(TopLevelType keep, IReadOnlyList<TopLevelType> fileLocal, bool renameWithheld)
    {
        var names = fileLocal.Select(type => type.Key).Distinct(StringComparer.Ordinal).ToArray();
        var classes = fileLocal.Append(keep).Count(type => type.Node.IsKind(SyntaxKind.ClassDeclaration) || type.Node.IsKind(SyntaxKind.RecordDeclaration));
        var stayed = StayNotePrefix + (names.Length == 1 ? " stayed in this file: " : "s stayed in this file: ")
            + string.Join(", ", names)
            + (classes >= 2 ? "; SA1402 still flags the file" : "; SA1402 still flags the file where it checks more than classes");
        return renameWithheld
            ? stayed + "; the file keeps its name because a rename changes a file-local type's metadata name"
            : stayed;
    }

    private static bool DeclaresExtensionMembers(TopLevelType type)
    {
        return type.Node.DescendantNodes().Any(node =>
            node.IsKind(SyntaxKind.ExtensionBlockDeclaration)
            || (node is MethodDeclarationSyntax method
                && method.ParameterList.Parameters.Any(parameter => parameter.Modifiers.Any(SyntaxKind.ThisKeyword))));
    }
}
