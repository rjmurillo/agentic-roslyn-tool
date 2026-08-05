using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticRoslynTool;

/// <summary>
/// The machine-readable result of one run, emitted by <c>--json</c>. The status
/// vocabulary and the field names mirror the CSV manifest exactly, so an agent that
/// learns one format already knows the other.
/// </summary>
internal sealed record RunReport(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("manifest")] string Manifest,
    [property: JsonPropertyName("summary")] RunSummary Summary,
    [property: JsonPropertyName("files")] IReadOnlyList<FileReport> Files)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Builds the report from the manifest rows a run produced.</summary>
    public static RunReport Create(IReadOnlyList<FileResult> results, Phase phase, string manifestPath)
    {
        var summary = new RunSummary(
            results.Count,
            results.Count(r => IsStatus(r, "split")),
            results.Count(r => IsStatus(r, "skipped")),
            results.Count(r => IsStatus(r, "failed")),
            results.Sum(r => r.NewFiles.Count));

        var files = results
            .Select(r => new FileReport(r.OriginalPath, r.KeptPath, r.GitMove, r.Status, r.Reason, r.Note, r.NewFiles.Select(f => new NewFileReport(f.Path, f.Type)).ToArray()))
            .ToArray();

        return new RunReport(PhaseName(phase), manifestPath, summary, files);
    }

    /// <summary>Serializes the report as indented JSON with a trailing newline omitted.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>A single line of human-readable counts, safe to write to standard error.</summary>
    public string ToSummaryLine() =>
        $"{Phase}: {Summary.Total} input(s), {Summary.Split} split, {Summary.Skipped} skipped, {Summary.Failed} failed, {Summary.NewFiles} new file(s).";

    // Exact match, not StartsWith: the counts are the contract, and a prefix match would
    // silently miscount the day a status name gains a suffix.
    private static bool IsStatus(FileResult result, string status) =>
        StringComparer.OrdinalIgnoreCase.Equals(result.Status, status);

    private static string PhaseName(Phase phase) => phase switch
    {
        AgenticRoslynTool.Phase.Plan => "plan",
        AgenticRoslynTool.Phase.Renames => "renames",
        _ => "content",
    };
}
