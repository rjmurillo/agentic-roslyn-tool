namespace AgenticRoslynTool;

/// <summary>
/// Manifest row describing one new sibling file produced by a split. Emitted per split
/// row of the CSV manifest, alongside the parent <see cref="FileResult"/>.
/// </summary>
/// <param name="Path">Absolute path where the new file will live.</param>
/// <param name="Type">The <see cref="TopLevelType.Key"/> value for the type placed in the new file.</param>
internal sealed record NewFileResult(string Path, string Type);
