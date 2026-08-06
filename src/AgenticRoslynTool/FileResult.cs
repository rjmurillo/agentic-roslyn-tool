namespace AgenticRoslynTool;

/// <summary>
/// One row in the split-types manifest, describing the outcome for a single input file.
/// The status vocabulary (<c>split</c>, <c>skipped</c>, <c>failed</c>) is what the
/// content phase reads back to decide which files to act on, so the strings are load
/// bearing and must not drift.
/// </summary>
/// <param name="OriginalPath">Absolute path of the input file, as read from the input list.</param>
/// <param name="KeptPath">
/// Absolute path where the kept file lives after the split. Equal to
/// <paramref name="OriginalPath"/> unless a rename was applied.
/// </param>
/// <param name="GitMove">True when the plan calls for the kept file to be moved via <c>git mv</c> in the renames phase. This records what the plan asked for, so it stays true on a later row once that move has landed. Compare <see cref="KeptPath"/> against the original path to tell whether the move is still owed.</param>
/// <param name="Status">One of <c>split</c>, <c>skipped</c>, or <c>failed</c>.</param>
/// <param name="Reason">Diagnostic text explaining a skip or failure.</param>
/// <param name="Note">Free-form supplementary note, for example the directive-safety summary.</param>
/// <param name="NewFiles">The set of new sibling files this row will create; empty for skips and failures.</param>
internal sealed record FileResult(string OriginalPath, string KeptPath, bool GitMove, string Status, string? Reason, string? Note, IReadOnlyList<NewFileResult> NewFiles)
{
    /// <summary>Constructs a successful split result.</summary>
    public static FileResult Split(string originalPath, string keptPath, bool gitMove, string? note, IReadOnlyList<NewFileResult> newFiles) => new(originalPath, keptPath, gitMove, "split", null, note, newFiles);

    /// <summary>Constructs a skip result with a diagnostic reason. Skips are not failures; they mean the tool refused to touch this file.</summary>
    public static FileResult Skip(string originalPath, string keptPath, string reason) => new(originalPath, keptPath, false, "skipped", reason, null, Array.Empty<NewFileResult>());

    /// <summary>Constructs a failure result. Failures record an attempted split that could not complete safely; no output is written for a failed row.</summary>
    public static FileResult Failed(string originalPath, string keptPath, string reason) => new(originalPath, keptPath, false, "failed", reason, null, Array.Empty<NewFileResult>());
}
