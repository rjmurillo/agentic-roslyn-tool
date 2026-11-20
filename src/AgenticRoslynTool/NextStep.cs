namespace AgenticRoslynTool;

/// <summary>
/// Works out the arguments for the phase that should run after this one. The JSON report
/// carries them as <c>nextArgs</c>, and standard error carries them as a <c>Next:</c> line.
/// </summary>
/// <remarks>
/// <para>
/// A caller that copies the next command exactly cannot drop a flag between phases. That
/// matters most for economical models, which follow a literal command more reliably than a
/// paragraph of advice. A frontier model ignores the field at no cost.
/// </para>
/// <para>
/// The arguments start at the verb and leave out the command name. The tool runs as
/// <c>agentic-roslyn-tool</c>, <c>dotnet agentic-roslyn-tool</c>, or
/// <c>dnx AgenticRoslynTool --</c>, and only the caller knows which.
/// </para>
/// <para>
/// <c>--collision-naming</c> is never repeated. The plan recorded it in the manifest, and a
/// later phase reads it from there. See decision 24.
/// </para>
/// </remarks>
internal static class NextStep
{
    /// <summary>
    /// The arguments for the next phase, or null when there is no safe next phase to suggest.
    /// </summary>
    /// <param name="options">The options this run was given.</param>
    /// <param name="phase">The phase that just ran.</param>
    /// <param name="reportRows">The rows describing what this run did.</param>
    /// <returns>
    /// Null after <c>content</c>, when any row failed, when no row is <c>split</c>, and when
    /// the input was standard input, which a second process cannot read again.
    /// </returns>
    public static IReadOnlyList<string>? For(Options options, Phase phase, IReadOnlyList<FileResult> reportRows)
    {
        if (phase == Phase.Content
            || options.InputPath == Options.StdinPath
            || reportRows.Any(r => r.Status == "failed")
            || !reportRows.Any(r => r.Status == "split"))
        {
            return null;
        }

        // Renames exists only to give git a rename it can record. With nothing to move it
        // rewrites the manifest and nothing else, so suggesting it would spend a turn.
        var next = phase == Phase.Plan && reportRows.Any(r => r.Status == "split" && r.GitMove)
            ? "renames"
            : "content";

        var args = new List<string>
        {
            "split-types", "--phase", next,
            "--input", options.InputPath,
            "--manifest", options.ManifestPath,
            "--repo-root", options.RepoRoot,
        };

        if (options.RequiredHeader is not null)
        {
            args.Add("--require-header");
            args.Add(options.RequiredHeader);
        }

        foreach (var exclude in options.Excludes ?? [])
        {
            args.Add("--exclude");
            args.Add(exclude);
        }

        if (options.Json)
        {
            args.Add("--json");
        }

        return args;
    }

    /// <summary>
    /// The one line written to standard error for <paramref name="args"/>. Quotes an argument
    /// that holds a space. Falls back to a sentence when an argument holds a quote or a line
    /// break, because no quoting is right for every shell, and a wrong one is worse than none.
    /// </summary>
    /// <param name="args">The value <see cref="For"/> returned.</param>
    /// <param name="phase">The phase that just ran.</param>
    public static string Describe(IReadOnlyList<string> args, Phase phase)
    {
        var commitFirst = phase == Phase.Renames ? "commit the renames, then " : string.Empty;
        if (args.Any(a => a.Contains('"', StringComparison.Ordinal) || a.Contains('\n', StringComparison.Ordinal) || a.Contains('\r', StringComparison.Ordinal)))
        {
            return $"Next: {commitFirst}run {args[1]} {args[2]} with the same options. --json prints the exact arguments as nextArgs.";
        }

        var line = string.Join(' ', args.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
        return $"Next: {commitFirst}run <tool command> {line}";
    }
}
