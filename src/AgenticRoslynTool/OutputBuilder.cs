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
    /// differ by arity are caught earlier in <see cref="SplitPlanner.BuildPlan"/> and refused as
    /// <c>target path collision within split</c>.
    /// </summary>
    /// <remarks>
    /// Under <see cref="FileNamingConvention.Metadata"/> every generic type is named
    /// <c>Name`N.cs</c> with N its arity, whether or not a sibling shares its name, because
    /// that is the only name SA1649 accepts for it under that convention. Two generic types
    /// with one name and one arity then map to the same file and are refused by the same
    /// collision check. The convention has no default, for the reason
    /// <see cref="SplitPlanner.BuildPlan"/> gives: a caller that forgot it would plan
    /// <c>stylecop</c> names inside a <c>metadata</c> run.
    /// </remarks>
    internal static string GetFileName(TopLevelType type, IReadOnlyList<TopLevelType> allTypes, FileNamingConvention convention)
    {
        if (convention == FileNamingConvention.Metadata)
        {
            return GetMetadataName(type) + ".cs";
        }

        var sameName = allTypes.Where(t => StringComparer.Ordinal.Equals(t.Name, type.Name)).ToArray();
        if (type.TypeParameters.Count > 0 && sameName.Length > 1)
        {
            return type.Name + "{" + string.Join(",", type.TypeParameters) + "}.cs";
        }

        return type.Name + ".cs";
    }

    /// <summary>
    /// The name SA1649 expects for a type under the metadata convention, without the
    /// extension: <c>Name`N</c> for a generic type of arity N, and <c>Name</c> otherwise.
    /// </summary>
    internal static string GetMetadataName(TopLevelType type)
    {
        return type.TypeParameters.Count == 0
            ? type.Name
            : type.Name + "`" + type.TypeParameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Materializes the in-memory output text for every planned file. For each planned
    /// output, every top-level type it does not hold (see <see cref="PlannedFile.Holds"/>) is removed with
    /// <c>SyntaxRemoveOptions.KeepUnbalancedDirectives</c> to prevent orphaned
    /// preprocessor tokens, assembly-level attributes are cleared on non-kept outputs
    /// (attributes stay only in the kept file), and the tree is run through
    /// <see cref="OwnedSpanCollapser"/> before serialization. The collapser leaves each
    /// remaining type's owned span untouched, because the verifier requires that text to
    /// appear unchanged. Last, <see cref="NullableDirectives.Restore"/> restates the
    /// nullable context each kept type had in the source, which a removed type may have
    /// carried away. The header is injected
    /// after body text is captured so line-conservation counting is not confused by
    /// it.
    /// </summary>
    internal static OutputFile[] BuildOutputs(EncodedSource source, CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, SplitPlan plan, string headerText)
    {
        var header = headerText.Length == 0 ? string.Empty : headerText + source.NewLine + source.NewLine;
        var outputs = new List<OutputFile>();
        foreach (var file in plan.Files)
        {
            var newRoot = Carve(root, types, file);
            if (!file.IsKept)
            {
                newRoot = newRoot.WithAttributeLists(default);
            }

            newRoot = OwnedSpanCollapser.Collapse(newRoot);
            var (restoredRoot, addedLines) = NullableDirectives.Restore(newRoot, file.Type, source.NewLine, headerText);
            var rendered = restoredRoot.ToFullString();
            var body = EnsureTrailingNewLine(rendered, source.NewLine, source.HasFinalNewLine);
            var text = EnsureHeader(rendered, header);
            text = EnsureTrailingNewLine(text, source.NewLine, source.HasFinalNewLine);
            outputs.Add(new OutputFile(file.Path, file.Type, text, body, addedLines));
        }

        return outputs.ToArray();
    }

    /// <summary>
    /// Removes every type <paramref name="file"/> does not hold from the source tree, keeping
    /// any directive left unbalanced. <see cref="PlannedFile.Holds"/> decides, so the kept file
    /// keeps its <c>file</c> types. <see cref="BuildOutputs"/> and
    /// <see cref="DirectiveAnalyzer.ProducesEmptyDirectiveShell"/> both start from this carve.
    /// <see cref="BuildOutputs"/> then clears assembly attributes on the outputs that are not
    /// kept and collapses blank lines, which the guard does not see.
    /// </summary>
    internal static CompilationUnitSyntax Carve(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, PlannedFile file)
    {
        var remove = types.Where(t => !file.Holds(t)).Select(t => t.Node).ToArray();
        return root.RemoveNodes(remove, SyntaxRemoveOptions.KeepUnbalancedDirectives) ?? root;
    }

    /// <summary>
    /// Normalizes a supplied required-header string to the source file's own newline
    /// style and strips whitespace at both ends, so a banner supplied with a stray leading
    /// or trailing space is injected without it. The normalized value is used both for
    /// injection and for the check in <see cref="OutputVerifier.VerifyOutputs"/>;
    /// they must stay in sync or a multi-line header supplied with <c>\n</c> against
    /// a CRLF source would fail verification and refuse to split. Leading whitespace
    /// goes because the verifier requires the banner at offset zero, so a header that
    /// kept it could never be found where it was just written.
    /// </summary>
    internal static string NormalizeHeaderText(string? requiredHeader, string newLine)
    {
        if (string.IsNullOrWhiteSpace(requiredHeader))
        {
            return string.Empty;
        }

        return requiredHeader.Trim().Replace("\r\n", "\n").Replace("\n", newLine);
    }

    /// <summary>
    /// Returns the offset after a matching header, or null when it is absent.
    /// The match is anchored at the end of a line, so a file starting with <c>// B-extra</c>
    /// does not satisfy a required header of <c>// B</c>, while trailing spaces on the banner
    /// line still count as the same banner. Callers may hold the header in either form the
    /// pipeline produces: bare, or followed by blank lines. This is the single probe
    /// behind both <see cref="EnsureHeader"/> and <see cref="OutputVerifier.VerifyOutputs"/>;
    /// they must agree or a file would have the banner declared present and then be rejected
    /// for not having it.
    /// </summary>
    internal static int? MatchHeaderEnd(string text, string? header, bool allowLeadingWhitespace = false)
    {
        var probe = header?.Trim() ?? string.Empty;
        var body = allowLeadingWhitespace ? text.AsSpan().TrimStart() : text.AsSpan();
        if (probe.Length == 0)
        {
            return 0;
        }

        if (!body.StartsWith(probe, StringComparison.Ordinal))
        {
            return null;
        }

        var index = probe.Length;
        while (index < body.Length && body[index] is ' ' or '\t')
        {
            index++;
        }

        return index == body.Length || body[index] is '\r' or '\n' ? text.Length - body.Length + probe.Length : null;
    }

    /// <summary>
    /// Prepends the required header when the text does not already start with it.
    /// A file that already carries the banner does not receive a second copy and keeps
    /// its spacing below the banner. Leading whitespace above the banner is dropped in
    /// both branches, because <see cref="OutputVerifier.VerifyOutputs"/> requires the
    /// header at offset zero.
    /// </summary>
    internal static string EnsureHeader(string text, string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return text;
        }

        var trimmed = text.TrimStart();
        return MatchHeaderEnd(text, header, allowLeadingWhitespace: true) is not null ? trimmed : header + trimmed;
    }

    internal static string EnsureTrailingNewLine(string text, string newLine, bool hasFinalNewLine)
    {
        var trimmed = text.TrimEnd('\r', '\n');
        return hasFinalNewLine ? trimmed + newLine : trimmed;
    }
}
