using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool;

/// <summary>
/// Decides whether a file's preprocessor directives survive being split, and whether the
/// split would leave a visibly empty region behind.
/// </summary>
/// <remarks>
/// Split out of <see cref="FileSplitter"/> because this reasons about one syntax tree and
/// nothing else. It reads no options, touches no disk, and returns a verdict rather than
/// acting on one, so a directive shape can be pinned by parsing a string.
/// </remarks>
internal static class DirectiveAnalyzer
{
    /// <summary>
    /// Refuses to split when any planned output would leave a preprocessor region
    /// (a <c>#region</c> or <c>#if</c>) containing only whitespace and nested
    /// directives. Prevents the visible artefact of an "empty shell" region that
    /// makes the split obviously wrong to a human reader even when it parses.
    /// </summary>
    internal static bool ProducesEmptyDirectiveShell(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, SplitPlan plan)
    {
        foreach (var file in plan.Files)
        {
            var remove = types.Where(t => !ReferenceEquals(t, file.Type)).Select(t => t.Node).ToArray();
            var newRoot = root.RemoveNodes(remove, SyntaxRemoveOptions.KeepUnbalancedDirectives) ?? root;
            if (ContainsEmptyDirectiveGroup(newRoot.ToFullString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Scans one candidate output's rendered text for a preprocessor region that would be left containing only whitespace or nested directives.</summary>
    internal static bool ContainsEmptyDirectiveGroup(string text)
    {
        var lines = SplitLineSegments(text).ToList();
        var stack = new Stack<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var directive = GetDirectiveKind(lines[i]);
            if (directive is "#region" or "#if")
            {
                stack.Push(i);
                continue;
            }

            if (directive is not "#endregion" and not "#endif")
            {
                continue;
            }

            if (stack.Count == 0)
            {
                continue;
            }

            var start = stack.Pop();
            if (HasOnlyWhitespaceOrDirectives(lines, start, i))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasOnlyWhitespaceOrDirectives(IReadOnlyList<string> lines, int start, int end)
    {
        for (var i = start + 1; i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(TrimLineEnding(lines[i])) || GetDirectiveKind(lines[i]) is not null)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    internal static string? GetDirectiveKind(string line)
    {
        var trimmed = TrimLineEnding(line).TrimStart();
        foreach (var directive in new[] { "#region", "#endregion", "#if", "#elif", "#else", "#endif" })
        {
            if (trimmed.StartsWith(directive, StringComparison.Ordinal))
            {
                return directive;
            }
        }

        return null;
    }

    internal static string TrimLineEnding(string line)
    {
        return line.TrimEnd('\r', '\n');
    }

    internal static IEnumerable<string> SplitLineSegments(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
            {
                continue;
            }

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            yield return text[start..(i + 1)];
            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>
    /// Analyzes preprocessor directives (<c>#if/#elif/#else/#endif</c> and
    /// <c>#region/#endregion</c>) and refuses to split when any directive group spans
    /// more than one top-level type. Splitting an unbalanced directive group would
    /// leave dangling directives in at least one output.
    /// </summary>
    /// <returns>A <see cref="DirectiveSafety"/> whose <see cref="DirectiveSafety.IsSafe"/> is true only when every directive group is contained within a single top-level type.</returns>
    internal static DirectiveSafety AnalyzeDirectiveSafety(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types)
    {
        var directives = root.DescendantTrivia(descendIntoTrivia: true)
            .Where(IsUnsafeDirective)
            .Select(trivia => DirectiveInfo.Create(root.SyntaxTree, trivia, FindOwningType(trivia.SpanStart, types)))
            .ToArray();
        if (directives.Length == 0)
        {
            return DirectiveSafety.Safe(null);
        }

        var regionStack = new Stack<List<DirectiveInfo>>();
        var ifStack = new Stack<List<DirectiveInfo>>();
        var groups = new List<List<DirectiveInfo>>();
        foreach (var directive in directives)
        {
            switch (directive.Trivia.Kind())
            {
                case SyntaxKind.RegionDirectiveTrivia:
                    regionStack.Push(new List<DirectiveInfo> { directive });
                    break;
                case SyntaxKind.EndRegionDirectiveTrivia:
                    if (regionStack.Count == 0)
                    {
                        return DirectiveSafety.Unsafe($"{directive.Kind} directive at line {directive.Line} has no matching #region");
                    }

                    var regionGroup = regionStack.Pop();
                    regionGroup.Add(directive);
                    groups.Add(regionGroup);
                    break;
                case SyntaxKind.IfDirectiveTrivia:
                    ifStack.Push(new List<DirectiveInfo> { directive });
                    break;
                case SyntaxKind.ElifDirectiveTrivia:
                case SyntaxKind.ElseDirectiveTrivia:
                    if (ifStack.Count == 0)
                    {
                        return DirectiveSafety.Unsafe($"{directive.Kind} directive at line {directive.Line} has no matching #if");
                    }

                    ifStack.Peek().Add(directive);
                    break;
                case SyntaxKind.EndIfDirectiveTrivia:
                    if (ifStack.Count == 0)
                    {
                        return DirectiveSafety.Unsafe($"{directive.Kind} directive at line {directive.Line} has no matching #if");
                    }

                    var ifGroup = ifStack.Pop();
                    ifGroup.Add(directive);
                    groups.Add(ifGroup);
                    break;
            }
        }

        if (regionStack.Count != 0)
        {
            var directive = regionStack.Peek()[0];
            return DirectiveSafety.Unsafe($"#region directive at line {directive.Line} has no matching #endregion");
        }

        if (ifStack.Count != 0)
        {
            var directive = ifStack.Peek()[0];
            return DirectiveSafety.Unsafe($"#if directive at line {directive.Line} has no matching #endif");
        }

        var owners = new Dictionary<int, TopLevelType>();
        foreach (var group in groups)
        {
            var owner = GetDirectiveGroupOwner(group, types);
            if (owner is null)
            {
                return DirectiveSafety.Unsafe($"{group[0].Kind} group at line {group[0].Line} crosses top-level type boundaries");
            }

            foreach (var directive in group)
            {
                owners[directive.Trivia.SpanStart] = owner;
            }
        }

        return DirectiveSafety.Safe("safe directives: " + string.Join("; ", directives.Select(d => $"{d.Kind} line {d.Line} -> {owners[d.Trivia.SpanStart].Key}")));
    }

    /// <summary>Finds the top-level type whose full span uniquely contains a position, or null when zero or more than one type qualifies.</summary>
    internal static TopLevelType? FindOwningType(int position, IReadOnlyList<TopLevelType> types)
    {
        var matches = types.Where(type => type.Node.FullSpan.Start <= position && position < type.Node.FullSpan.End).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Resolves the single top-level type that a directive group belongs to. A group is
    /// considered owned when exactly one type overlaps its span, or when exactly one
    /// type is fully contained within it. Anything else is treated as crossing type
    /// boundaries and results in a split refusal.
    /// </summary>
    internal static TopLevelType? GetDirectiveGroupOwner(IReadOnlyList<DirectiveInfo> group, IReadOnlyList<TopLevelType> types)
    {
        var groupStart = group.Min(d => d.Trivia.SpanStart);
        var groupEnd = group.Max(d => d.Trivia.Span.End);
        var overlappingTypes = types.Where(type => type.Node.SpanStart < groupEnd && groupStart < type.Node.Span.End).ToArray();
        if (overlappingTypes.Length == 1)
        {
            return overlappingTypes[0];
        }

        var containedTypes = types.Where(type => groupStart <= type.Node.SpanStart && type.Node.Span.End <= groupEnd).ToArray();
        return containedTypes.Length == 1 ? containedTypes[0] : null;
    }

    internal static bool IsUnsafeDirective(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.IfDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.ElifDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.ElseDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.RegionDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia);
    }
}
