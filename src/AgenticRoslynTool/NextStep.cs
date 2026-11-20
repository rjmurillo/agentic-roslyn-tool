using System.Buffers;

namespace AgenticRoslynTool;

/// <summary>
/// Works out the arguments for the phase that should run after this one. The JSON report
/// carries them as <c>nextArgs</c>, and standard error carries them as a <c>Next:</c> line.
/// </summary>
/// <remarks>
/// <para>
/// A caller that copies the next command exactly cannot drop a flag between phases. This is
/// expected to help economical models most. The NuGet team measured lower skill compliance
/// in weaker models, and one literal command asks for less compliance than a paragraph of
/// advice. No evaluation of this tool has measured it. A frontier model ignores the field.
/// </para>
/// <para>
/// The arguments start at the verb and leave out the command name. The tool runs as
/// <c>agentic-roslyn-tool</c>, <c>dotnet agentic-roslyn-tool</c>, or
/// <c>dnx AgenticRoslynTool --</c>, and only the caller knows which.
/// </para>
/// <para>
/// <c>--collision-naming</c> is never repeated. The plan recorded it in the manifest, and a
/// later phase reads it from there. See decision 26.
/// </para>
/// </remarks>
internal static class NextStep
{
    /// <summary>Characters printed bare in the <c>Next:</c> line, because no shell gives them a meaning.</summary>
    private static readonly SearchValues<char> Bare = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_./:=,+@%-");

    /// <summary>The arguments for the next phase, or null when there is no safe next phase to suggest.</summary>
    /// <param name="options">The options this run was given, including the phase that ran.</param>
    /// <param name="reportRows">The rows describing what this run did.</param>
    /// <returns>
    /// Null after <c>content</c>, when any row failed, when no row is <c>split</c>, and when
    /// the input was standard input, which a second process cannot read again.
    /// </returns>
    public static IReadOnlyList<string>? For(Options options, IReadOnlyList<FileResult> reportRows)
    {
        if (options.Phase == Phase.Content
            || options.InputPath == Options.StdinPath
            || reportRows.Any(r => r.Status == "failed")
            || !reportRows.Any(r => r.Status == "split"))
        {
            return null;
        }

        // Renames exists only to give git a rename it can record. With nothing to move it
        // does nothing a caller needs, so suggesting it would spend a turn.
        var next = options.Phase == Phase.Plan && reportRows.Any(r => r.Status == "split" && r.GitMove)
            ? "renames"
            : "content";

        // Every option this run had that the next phase accepts. The parser fills in the
        // default report path when --report is absent, so only a chosen path is carried.
        var chosenReport = options.ReportPath is { } report && !StringComparer.Ordinal.Equals(report, Options.DefaultReportPath(options.ManifestPath))
            ? report
            : null;
        (string Flag, string? Value)[] carried =
        [
            ("--phase", next),
            ("--input", options.InputPath),
            ("--manifest", options.ManifestPath),
            ("--repo-root", options.RepoRoot),
            ("--report", chosenReport),
            ("--require-header", options.RequiredHeader),
            .. (options.Excludes ?? []).Select(exclude => ("--exclude", (string?)exclude)),
        ];

        var args = new List<string> { "split-types" };
        foreach (var (flag, value) in carried)
        {
            if (value is not null)
            {
                args.AddRange([flag, value]);
            }
        }

        if (options.Json)
        {
            args.Add("--json");
        }

        return args;
    }

    /// <summary>The one line written to standard error for <paramref name="args"/>.</summary>
    /// <remarks>
    /// An argument made only of characters no shell interprets is printed bare. One that
    /// also holds a space or a backslash is put in single quotes, which bash, zsh, and
    /// PowerShell all read literally. Anything else, such as <c>$</c>, a backtick, a quote,
    /// <c>&amp;</c>, or a line break, turns the line into a sentence. No quoting is right for
    /// every shell, and a wrong one is worse than none. <c>nextArgs</c> holds the exact
    /// arguments in that case.
    /// </remarks>
    /// <param name="args">The value <see cref="For"/> returned.</param>
    /// <param name="ranPhase">The phase that just ran. After renames, the caller commits first.</param>
    public static string Describe(IReadOnlyList<string> args, Phase ranPhase)
    {
        var phaseIndex = args.ToList().IndexOf("--phase");
        var next = phaseIndex >= 0 && phaseIndex + 1 < args.Count ? args[phaseIndex + 1] : "the next phase";
        var commitFirst = ranPhase == Phase.Renames ? "commit the renames, then " : string.Empty;
        if (!args.All(IsQuotable))
        {
            return $"Next: {commitFirst}run --phase {next} with the same options. --json prints the exact arguments as nextArgs.";
        }

        var line = string.Join(' ', args.Select(a => a.Length == 0 || a.AsSpan().ContainsAnyExcept(Bare) ? $"'{a}'" : a));
        return $"Next: {commitFirst}run <tool command> {line}";
    }

    private static bool IsQuotable(string arg) => arg.All(c => Bare.Contains(c) || c is ' ' or '\\');
}
