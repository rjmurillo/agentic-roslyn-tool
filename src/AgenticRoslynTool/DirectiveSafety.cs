namespace AgenticRoslynTool;

/// <summary>
/// Result of directive analysis for one input file. The tool refuses to split when any
/// <c>#if</c>, <c>#region</c>, or their partners span more than one top-level type,
/// because splitting would leave unbalanced directives in at least one output.
/// </summary>
/// <param name="IsSafe">True when every directive group is fully contained within a single top-level type.</param>
/// <param name="Reason">Human-readable explanation of why the split was refused; null when safe.</param>
/// <param name="Note">Optional diagnostic summary of the safe directive groups; propagated into the manifest note.</param>
internal sealed record DirectiveSafety(bool IsSafe, string? Reason, string? Note)
{
    /// <summary>Factory for the safe outcome, optionally carrying a diagnostic note.</summary>
    public static DirectiveSafety Safe(string? note) => new(true, null, note);

    /// <summary>Factory for the unsafe outcome; the reason is surfaced as the manifest skip reason.</summary>
    public static DirectiveSafety Unsafe(string reason) => new(false, reason, null);
}
