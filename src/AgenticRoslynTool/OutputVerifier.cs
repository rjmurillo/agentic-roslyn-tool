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
    /// across the outputs (never dropped, never duplicated);
    /// (4) non-whitespace lines are conserved across the split
    /// (see <see cref="VerifyLineConservation"/>);
    /// (5) the set of top-level types in the outputs matches the input set exactly; and
    /// (6) each output's type keeps its source nullable context
    /// (see <see cref="VerifyNullableContext"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown as soon as any invariant fails, so the failure is surfaced without partial output being written.</exception>
    internal static void VerifyOutputs(string originalPath, IReadOnlyList<TopLevelType> inputTypes, IReadOnlyList<OutputFile> outputs, CSharpParseOptions parseOptions, string? requiredHeader)
    {
        foreach (var output in outputs)
        {
            if (!OutputBuilder.StartsWithHeader(output.Text, requiredHeader))
            {
                throw new InvalidOperationException($"{output.Path} does not start with required file header");
            }

            var tree = CSharpSyntaxTree.ParseText(output.Text, parseOptions, path: output.Path);
            var diagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (diagnostics.Length != 0)
            {
                throw new InvalidOperationException($"{output.Path} does not parse: {JoinDiagnostics(diagnostics)}");
            }
        }

        foreach (var inputType in inputTypes)
        {
            var declaration = GetTypeOwnedText(inputType);
            var count = outputs.Sum(o => CountOccurrences(o.Text, declaration));
            if (count != 1)
            {
                throw new InvalidOperationException($"{originalPath}: declaration and owned trivia for {inputType.Key} appears {count} times across outputs");
            }
        }

        VerifyLineConservation(originalPath, inputTypes, outputs);

        var outputTypes = outputs.SelectMany(o => TopLevelType.Find(CSharpSyntaxTree.ParseText(o.Text, parseOptions, path: o.Path).GetCompilationUnitRoot())).Select(t => t.Key).Order(StringComparer.Ordinal).ToArray();
        var inputKeys = inputTypes.Select(t => t.Key).Order(StringComparer.Ordinal).ToArray();
        if (!inputKeys.SequenceEqual(outputTypes, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{originalPath}: output top-level type set does not match input");
        }

        VerifyNullableContext(originalPath, outputs, parseOptions);
    }

    /// <summary>
    /// Checks that each output's own type reads the same nullable context in the output text
    /// as it did in the source. The output is parsed again from <see cref="OutputFile.Text"/>,
    /// which is what reaches disk, so a misplaced or dropped <c>#nullable</c> line fails the
    /// file before it is written.
    /// </summary>
    /// <remarks>
    /// This check uses the same fold as <see cref="NullableDirectives.Restore"/>, so it catches
    /// a misplaced line, not a wrong fold. Tests prove the fold against the compiler.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when an output lacks its own type, or when that type's annotations or warnings setting differs from the source.</exception>
    internal static void VerifyNullableContext(string originalPath, IReadOnlyList<OutputFile> outputs, CSharpParseOptions parseOptions)
    {
        foreach (var output in outputs)
        {
            var expected = NullableDirectives.SourceStateOf(output.Type);
            var root = CSharpSyntaxTree.ParseText(output.Text, parseOptions, path: output.Path).GetCompilationUnitRoot();
            var written = TopLevelType.Find(root).FirstOrDefault(t => StringComparer.Ordinal.Equals(t.Key, output.Type.Key))
                ?? throw new InvalidOperationException($"{originalPath}: {output.Path} does not declare {output.Type.Key}");
            var actual = NullableDirectives.StateAt(root, written.Node.SpanStart);
            if (actual != expected)
            {
                throw new InvalidOperationException($"{originalPath}: nullable context for {output.Type.Key} is {NullableDirectives.Describe(actual)} in {output.Path}, but {NullableDirectives.Describe(expected)} in the source");
            }
        }
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
    /// first, one for one, through <see cref="CountOutputLines"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a non-whitespace input line is not preserved across the outputs, when an output line that was not in the prologue appears more times in the outputs than in the input, or when an added line is not a <c>#nullable</c> directive the output holds.</exception>
    internal static void VerifyLineConservation(string originalPath, IReadOnlyList<TopLevelType> inputTypes, IReadOnlyList<OutputFile> outputs)
    {
        var sourceText = inputTypes[0].Node.SyntaxTree.GetText().ToString();
        var prologueLines = GetPrologueLines(sourceText, inputTypes);
        var originalLines = CountNonWhitespaceLines(sourceText);
        var outputLines = CountOutputLines(outputs);

        foreach (var (line, count) in originalLines)
        {
            outputLines.TryGetValue(line, out var outputCount);
            if (outputCount < count)
            {
                throw new InvalidOperationException($"{originalPath}: non-whitespace line was dropped: {line}");
            }
        }

        foreach (var (line, count) in outputLines)
        {
            originalLines.TryGetValue(line, out var originalCount);
            if (count > originalCount && !prologueLines.Contains(line))
            {
                throw new InvalidOperationException($"{originalPath}: non-prologue line was duplicated: {line}");
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
        var position = 0;
        foreach (var line in SplitLines(sourceText))
        {
            var lineEnd = position + line.Length;
            if (!string.IsNullOrWhiteSpace(line) && !ownedRanges.Any(range => position < range.End && lineEnd > range.Start))
            {
                result.Add(line);
            }

            position = lineEnd + GetLineBreakLength(sourceText, lineEnd);
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
    internal static Dictionary<string, int> CountOutputLines(IReadOnlyList<OutputFile> outputs)
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

    internal static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
            {
                continue;
            }

            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    internal static int GetLineBreakLength(string text, int lineEnd)
    {
        if (lineEnd >= text.Length)
        {
            return 0;
        }

        return text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1;
    }

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


    /// <summary>Renders a list of Roslyn diagnostics as a single semicolon-separated string, used to build refusal reasons.</summary>
    internal static string JoinDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        return string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.GetMessage()}"));
    }}
