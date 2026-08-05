namespace AgenticRoslynTool;

/// <summary>
/// Selects which stage of the split-types workflow the tool runs.
/// The stages are meant to run in sequence, each committed separately, so that
/// git records renames as renames and the content edit lands as its own change.
/// </summary>
internal enum Phase
{
    /// <summary>
    /// Compute the split and emit a CSV manifest without touching any source file.
    /// Selected by <c>--phase plan</c> or the <c>--dry-run</c> alias.
    /// </summary>
    Plan,

    /// <summary>
    /// Perform only the <c>git mv</c> step for files whose primary type does not match the
    /// current file name. Kept in a separate commit so git records renames rather than
    /// a delete plus an add.
    /// </summary>
    Renames,

    /// <summary>
    /// Default phase. Reads the plan manifest and writes the split output files, acting
    /// only on manifest rows whose status is <c>split</c>.
    /// </summary>
    Content,
}
