namespace AgenticRoslynTool;

/// <summary>
/// One planned output file produced by <see cref="FileSplitter"/>. Each planned file
/// pairs a target on-disk path with the top-level type that will live in it.
/// </summary>
/// <param name="Path">Absolute path of the output file, either the kept file or a new sibling.</param>
/// <param name="Type">The top-level type that will be the only type declaration in the output.</param>
/// <param name="IsKept">
/// True when this entry represents the file being kept in place (possibly under a new name
/// via <c>git mv</c>); false for every new sibling file created by the split.
/// </param>
internal sealed record PlannedFile(string Path, TopLevelType Type, bool IsKept)
{
    /// <summary>
    /// True when this output declares <paramref name="type"/>: its own type, and in the
    /// kept file every <c>file</c> type too, because a <c>file</c> type never leaves the
    /// file that declares it. Every other type is removed from this output.
    /// </summary>
    internal bool Holds(TopLevelType type) => ReferenceEquals(type, Type) || (IsKept && type.HasFileModifier);
}
