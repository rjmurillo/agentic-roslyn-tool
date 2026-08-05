using System.Text.Json.Serialization;

namespace AgenticRoslynTool;

/// <summary>
/// One manifest row in JSON form. The field names match the CSV manifest columns so
/// the two formats stay one contract rather than two.
/// </summary>
/// <param name="OriginalPath">Absolute path of the input file.</param>
/// <param name="KeptPath">Absolute path of the kept file after the split.</param>
/// <param name="GitMove">True when the renames phase must move the kept file.</param>
/// <param name="Status">One of <c>split</c>, <c>skipped</c>, or <c>failed</c>.</param>
/// <param name="Reason">Diagnostic text for a skip or a failure, otherwise null.</param>
/// <param name="Note">Supplementary note, for example the directive-safety summary.</param>
/// <param name="NewFiles">The files this row creates, each carrying its path and its type.</param>
internal sealed record FileReport(
    [property: JsonPropertyName("originalPath")] string OriginalPath,
    [property: JsonPropertyName("keptPath")] string KeptPath,
    [property: JsonPropertyName("gitMove")] bool GitMove,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("newFiles")] IReadOnlyList<NewFileReport> NewFiles);
