using System.Text.Json.Serialization;

namespace AgenticRoslynTool;

/// <summary>
/// One created file inside a JSON report row. Carries both manifest columns, so a caller
/// reading JSON has everything a caller reading CSV has.
/// </summary>
/// <param name="NewFilePath">Absolute path where the new file lives. Matches the CSV <c>newFilePath</c> column.</param>
/// <param name="Type">The type placed in that file. Matches the CSV <c>type</c> column.</param>
internal sealed record NewFileReport(
    [property: JsonPropertyName("newFilePath")] string NewFilePath,
    [property: JsonPropertyName("type")] string Type);
