using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AgenticRoslynTool;

/// <summary>
/// Runs every correctness check against the in-memory outputs, before any of them reach
/// disk. A failure here means the split is never written.
/// </summary>
/// <remarks>
/// Split out of <see cref="FileSplitter"/> because these are the invariants the tool is
/// judged on, and they were previously readable only by scrolling past the code that
/// produces the thing being checked. Nothing here writes, so a check can be exercised
/// against hand-built outputs.
/// </remarks>
internal static class OutputVerifier
{
    /// <summary>
    /// Runs every correctness check on the in-memory outputs before any file is
    /// written. The checks together protect these invariants:
    /// (1) every output starts with the required header when one was requested;
    /// (2) every output is a syntactically valid compilation unit;
    /// (3) each input type's declaration and owned trivia appears exactly once
    /// across the outputs (never dropped, never duplicated), or as often as the input
    /// holds that text when the input declares a <c>file</c> type;
    /// (4) non-whitespace lines are conserved across the split
    /// (see <see cref="VerifyLineConservation"/>);
    /// (5) the set of top-level types in the outputs matches the input set exactly; and
    /// (6) a <c>file</c> type stays in the file it was read from, no output that
    /// leaves that file names one, and every output keeps the input's state directives
    /// (see <see cref="FileLocalTypes.DescribeViolation"/>); and
    /// (7) each output's type keeps its source nullable context
    /// (see <see cref="NullableDirectives.Verify"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown as soon as any invariant fails, so the failure is surfaced without partial output being written.</exception>
    internal static void VerifyOutputs(string readPath, IReadOnlyList<TopLevelType> inputTypes, IReadOnlyList<OutputFile> outputs, string? requiredHeader)
    {
        foreach (var output in outputs)
        {
            if (OutputBuilder.MatchHeaderEnd(output.Text, requiredHeader) is null)
            {
                throw new InvalidOperationException($"{output.Path} does not start with required file header");
            }

            var tree = CSharpSyntaxTree.ParseText(output.Text, SourceParsing.Options, path: output.Path);
            var diagnostics = SourceParsing.Errors(tree);
            if (diagnostics.Length != 0)
            {
                throw new InvalidOperationException($"{output.Path} does not parse: {SourceParsing.Describe(diagnostics)}");
            }

            // The parse accepts #: and #! so a file-based program input can be read and
            // skipped. A project build rejects them (CS9298), so no output may carry one,
            // for example through --require-header.
            if (SourceParsing.IsFileBasedProgram(tree.GetCompilationUnitRoot()))
            {
                throw new InvalidOperationException($"{output.Path} holds a #: or #! directive, which a project build rejects");
            }
        }

        // A declaration's text appears once across the outputs. An input with a file-local
        // type is the exception, because that type shares the kept file with another type.
        // A second file-local type written the same way stays beside it. An ordinary type's
        // text can sit inside a file-local type's text, such as `class Helper { }` inside
        // `file class Helper { }`. So for such an input each text is expected as often as
        // the input itself holds it, counted the same way on both sides.
        var sourceText = inputTypes[0].Node.SyntaxTree.GetText().ToString();
        var hasFileLocal = inputTypes.Any(t => t.HasFileModifier);
        foreach (var inputType in inputTypes)
        {
            var declaration = GetTypeOwnedText(inputType);
            var expected = hasFileLocal ? CountOccurrences(sourceText, declaration) : 1;
            var count = outputs.Sum(o => CountOccurrences(o.Text, declaration));
            if (count != expected)
            {
                throw new InvalidOperationException($"{readPath}: declaration and owned trivia for {inputType.Key} appears {count} times across outputs, expected {expected}");
            }
        }

        VerifyLineConservation(readPath, inputTypes, outputs);

        var outputTypes = outputs.SelectMany(o => TopLevelType.Find(CSharpSyntaxTree.ParseText(o.Text, SourceParsing.Options, path: o.Path).GetCompilationUnitRoot())).Select(t => t.Key).Order(StringComparer.Ordinal).ToArray();
        var inputKeys = inputTypes.Select(t => t.Key).Order(StringComparer.Ordinal).ToArray();
        if (!inputKeys.SequenceEqual(outputTypes, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{readPath}: output top-level type set does not match input");
        }

        // A file-local type stays in the file it was read from, no output that leaves that
        // file may name one, and every output keeps the input's state directives. The
        // planner refuses such an input first. This asks again about the text that would
        // reach disk.
        if (FileLocalTypes.DescribeViolation(readPath, inputTypes, outputs) is { } violation)
        {
            throw new InvalidOperationException(violation);
        }

        NullableDirectives.Verify(readPath, outputs);
    }

    /// <summary>Returns the text of a type's declaration together with its owned leading trivia (doc comments and attributes).</summary>
    internal static string GetTypeOwnedText(TopLevelType type)
    {
        var fullText = type.Node.SyntaxTree.GetText().ToString();
        return fullText.Substring(GetTypeOwnedStart(type), type.Node.Span.End - GetTypeOwnedStart(type));
    }

    /// <summary>
    /// Returns the start position of a type's owned span. Ownership begins at the
    /// type's leading documentation comment when one exists; otherwise it begins at
    /// the type's declaration. This is what makes XML doc comments travel with the
    /// type they belong to.
    /// </summary>
    internal static int GetTypeOwnedStart(TopLevelType type)
    {
        foreach (var trivia in type.Node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                return trivia.SpanStart;
            }
        }

        return type.Node.SpanStart;
    }

    /// <summary>
    /// Counts non-whitespace source lines in the original file against the outputs
    /// and throws if a line was dropped, or if a non-prologue line was duplicated.
    /// This is the deepest correctness invariant of the tool: no line of user code is
    /// silently lost, and only prologue lines (lines outside every type's owned span,
    /// for example usings and the namespace, which legitimately appear in every
    /// output) are allowed to appear more than once across the outputs.
    /// </summary>
    /// <remarks>
    /// This method counts <see cref="OutputFile.BodyText"/>, which is the output text
    /// before any <c>--require-header</c> injection. That is deliberate: counting
    /// <see cref="OutputFile.Text"/> instead would let an injected header line mask a
    /// dropped source line. Each output's <see cref="OutputFile.AddedLines"/> are subtracted
    /// first, one for one, through <see cref="CountLinesAfterCheckingAdditions"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a non-whitespace input line is not preserved across the outputs, when an output line that was not in the prologue appears more times in the outputs than in the input, or when an added line is not a <c>#nullable</c> directive the output holds.</exception>
    internal static void VerifyLineConservation(string readPath, IReadOnlyList<TopLevelType> inputTypes, IReadOnlyList<OutputFile> outputs)
    {
        var sourceText = inputTypes[0].Node.SyntaxTree.GetText().ToString();
        var prologueLines = GetPrologueLines(sourceText, inputTypes);
        var originalLines = CountNonWhitespaceLines(sourceText);
        var outputLines = CountLinesAfterCheckingAdditions(outputs);

        foreach (var (line, count) in originalLines)
        {
            outputLines.TryGetValue(line, out var outputCount);
            if (outputCount < count)
            {
                throw new InvalidOperationException($"{readPath}: non-whitespace line was dropped: {line}");
            }
        }

        foreach (var (line, count) in outputLines)
        {
            originalLines.TryGetValue(line, out var originalCount);
            if (count > originalCount && !prologueLines.Contains(line))
            {
                throw new InvalidOperationException($"{readPath}: non-prologue line was duplicated: {line}");
            }
        }
    }

    /// <summary>
    /// Computes the set of "prologue" lines: non-whitespace source lines that fall
    /// outside every top-level type's owned span. These are the lines
    /// (usings, namespace declarations, and similar) that legitimately appear in
    /// every output file and are therefore exempt from the duplicate-line check in
    /// <see cref="VerifyLineConservation"/>.
    /// </summary>
    internal static HashSet<string> GetPrologueLines(string sourceText, IReadOnlyList<TopLevelType> inputTypes)
    {
        var ownedRanges = inputTypes
            .Select(type => (Start: GetTypeOwnedStart(type), End: type.Node.Span.End))
            .OrderBy(range => range.Start)
            .ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Microsoft.CodeAnalysis.Text.SourceText.From(sourceText).Lines)
        {
            var text = line.ToString();
            if (!string.IsNullOrWhiteSpace(text) && !ownedRanges.Any(range => line.Start < range.End && line.End > range.Start))
            {
                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>
    /// Counts the non-whitespace lines of every output's <see cref="OutputFile.BodyText"/>,
    /// less one copy of each line in its <see cref="OutputFile.AddedLines"/>.
    /// </summary>
    /// <remarks>
    /// It refuses an added line that is not a single <c>#nullable</c> directive, so the
    /// subtraction can never hide a duplicated source line of any other kind.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when an added line is not a <c>#nullable</c> directive, or is missing from its output.</exception>
    internal static Dictionary<string, int> CountLinesAfterCheckingAdditions(IReadOnlyList<OutputFile> outputs)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            var lines = CountNonWhitespaceLines(output.BodyText);
            foreach (var added in output.AddedLines)
            {
                if (!NullableDirectives.IsDirectiveLine(added))
                {
                    throw new InvalidOperationException($"{output.Path}: added line is not a #nullable directive: {added}");
                }

                if (!lines.TryGetValue(added, out var present) || present == 0)
                {
                    throw new InvalidOperationException($"{output.Path}: added line is missing from the output: {added}");
                }

                lines[added] = present - 1;
            }

            foreach (var (line, count) in lines)
            {
                result[line] = result.TryGetValue(line, out var existing) ? existing + count : count;
            }
        }

        return result;
    }

    internal static Dictionary<string, int> CountNonWhitespaceLines(string text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in SplitLines(text))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            result[line] = result.TryGetValue(line, out var count) ? count + 1 : 1;
        }

        return result;
    }

    /// <summary>Splits text at every line break the compiler reads, including U+0085, U+2028, and U+2029.</summary>
    /// <remarks>
    /// A split on CR and LF alone joined a restated <c>#nullable</c> line to the U+2028 before
    /// it, so the verifier refused a correct output.
    /// </remarks>
    internal static IEnumerable<string> SplitLines(string text) =>
        Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines.Select(line => line.ToString());

    /// <summary>Counts non-overlapping occurrences of <paramref name="value"/> in <paramref name="text"/>, using ordinal comparison.</summary>
    internal static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
