namespace AgenticRoslynTool;

/// <summary>
/// The result of one phase run, split into the rows that belong in the manifest and the
/// rows that describe what this run actually did.
/// </summary>
/// <remarks>
/// The two views are identical for the plan and renames phases. They diverge in the
/// content phase, which rewrites the manifest it read. A planned row the current input
/// list never mentioned must stay in the manifest verbatim, or applying content in
/// batches would destroy the reviewed plan for every batch after the first. The same
/// row must appear in the report as a skip, because this run did not apply it.
/// </remarks>
/// <param name="ManifestRows">Rows to serialize to the manifest CSV.</param>
/// <param name="ReportRows">Rows describing what this run did, used for the JSON report and the summary line.</param>
internal sealed record RunOutcome(IReadOnlyList<FileResult> ManifestRows, IReadOnlyList<FileResult> ReportRows)
{
    /// <summary>Constructs an outcome whose manifest and report views are the same rows.</summary>
    /// <param name="rows">The rows to use for both views.</param>
    public static RunOutcome Same(IReadOnlyList<FileResult> rows) => new(rows, rows);
}
