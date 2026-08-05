namespace AgenticRoslynTool;

/// <summary>
/// Immutable, positional record of a single new file the plan intends to emit for one
/// original input file. Used before the cross-file collision pass converts colliding
/// simple names into their qualified form.
/// </summary>
/// <param name="OriginalPath">Absolute path of the source file being split.</param>
/// <param name="Type">Type key, matching <see cref="TopLevelType.Key"/>, that will land in the output.</param>
/// <param name="Path">Absolute simple-name path the plan currently intends to write, for example <c>Foo.cs</c>.</param>
/// <param name="QualifiedPath">Absolute qualified fallback path, of the form <c>OriginalFileName.TypeName.cs</c>, used only when the simple-name path collides across two different source files.</param>
internal sealed record PlannedOutputEntry(string OriginalPath, string Type, string Path, string QualifiedPath);
