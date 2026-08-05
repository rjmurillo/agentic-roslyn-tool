using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Builds the in-memory text of every output file a split produces: the file name for
/// each type, the carved-out source, the required header, and the trailing newline.
/// </summary>
/// <remarks>
/// Split out of <see cref="FileSplitter"/> so that producing an output and checking one
/// (<see cref="OutputVerifier"/>) are separate readable units. Nothing here touches disk;
/// the result is handed back to the caller to write or discard.
/// </remarks>
internal static class OutputBuilder
{
    /// <summary>
    /// Picks the output file name for one type. Two types with the same simple name
    /// in the same input are disambiguated by generic arity as
    /// <c>Name{T1,T2}.cs</c>. Two same-simple-name types in one input that do NOT
    /// differ by arity are caught earlier in <see cref="BuildPlan"/> and refused as
    /// <c>target path collision within split</c>.
    /// </summary>
    internal static string GetFileName(TopLevelType type, IReadOnlyList<TopLevelType> allTypes)
    {
        var sameName = allTypes.Where(t => StringComparer.Ordinal.Equals(t.Name, type.Name)).ToArray();
        if (type.TypeParameters.Count > 0 && sameName.Length > 1)
        {
            return type.Name + "{" + string.Join(",", type.TypeParameters) + "}.cs";
        }

        return type.Name + ".cs";
    }

    /// <summary>
    /// Materializes the in-memory output text for every planned file. For each planned
    /// output, all other top-level types are removed with
    /// <c>SyntaxRemoveOptions.KeepUnbalancedDirectives</c> to prevent orphaned
    /// preprocessor tokens, assembly-level attributes are cleared on non-kept outputs
    /// (attributes stay only in the kept file), and the tree is run through
    /// <see cref="BlankLineCollapser"/> before serialization. The header is injected
    /// after body text is captured so line-conservation counting is not confused by
    /// it.
    /// </summary>
    internal static OutputFile[] BuildOutputs(EncodedSource source, CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, SplitPlan plan, string headerText)
    {
        var header = headerText.Length == 0 ? string.Empty : headerText + source.NewLine + source.NewLine;
        var outputs = new List<OutputFile>();
        foreach (var file in plan.Files)
        {
            var remove = types.Where(t => !ReferenceEquals(t, file.Type)).Select(t => t.Node).ToArray();
            var newRoot = root.RemoveNodes(remove, SyntaxRemoveOptions.KeepUnbalancedDirectives) ?? root;
            if (!file.IsKept)
            {
                newRoot = newRoot.WithAttributeLists(default);
            }

            newRoot = (CompilationUnitSyntax)new BlankLineCollapser().Visit(newRoot)!;
            var rendered = newRoot.ToFullString();
            var body = EnsureTrailingNewLine(rendered, source.NewLine, source.HasFinalNewLine);
            var text = EnsureHeader(rendered, header);
            text = EnsureTrailingNewLine(text, source.NewLine, source.HasFinalNewLine);
            outputs.Add(new OutputFile(file.Path, file.Type, text, body));
        }

        return outputs.ToArray();
    }

    /// <summary>
    /// Normalizes a supplied required-header string to the source file's own newline
    /// style and strips any trailing newlines. The normalized value is used both for
    /// injection and for the <c>StartsWith</c> check in <see cref="VerifyOutputs"/>;
    /// they must stay in sync or a multi-line header supplied with <c>\n</c> against
    /// a CRLF source would fail verification and refuse to split.
    /// </summary>
    internal static string NormalizeHeaderText(string? requiredHeader, string newLine)
    {
        if (string.IsNullOrWhiteSpace(requiredHeader))
        {
            return string.Empty;
        }

        return requiredHeader.Replace("\r\n", "\n").Replace("\n", newLine).TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Prepends the required header when the text does not already start with it.
    /// A file that already carries the banner keeps its existing spacing and does
    /// not receive a second copy.
    /// </summary>
    internal static string EnsureHeader(string text, string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return text;
        }

        var headerProbe = header.TrimStart();
        if (headerProbe.Length == 0)
        {
            return text;
        }

        return text.TrimStart().StartsWith(headerProbe.TrimEnd(), StringComparison.Ordinal) ? text : header + text.TrimStart();
    }

    internal static string EnsureTrailingNewLine(string text, string newLine, bool hasFinalNewLine)
    {
        var trimmed = text.TrimEnd('\r', '\n');
        return hasFinalNewLine ? trimmed + newLine : trimmed;
    }
}
