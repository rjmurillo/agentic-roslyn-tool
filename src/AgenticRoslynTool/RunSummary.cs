using System.Text.Json.Serialization;

namespace AgenticRoslynTool;

/// <summary>Aggregate counts for a run, so an agent never has to tally manifest rows itself.</summary>
/// <param name="Total">Number of manifest rows, which is one per distinct input path.</param>
/// <param name="Split">Rows whose status is <c>split</c>.</param>
/// <param name="Skipped">Rows whose status is <c>skipped</c>. A skip is a deliberate refusal, not an error.</param>
/// <param name="Failed">Rows whose status is <c>failed</c>. Any non-zero value makes the process exit 1.</param>
/// <param name="NewFiles">Total sibling files the run created or planned to create.</param>
internal sealed record RunSummary(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("split")] int Split,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("newFiles")] int NewFiles);
