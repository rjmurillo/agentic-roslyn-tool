namespace AgenticRoslynTool;

/// <summary>
/// How the cross-file collision pass qualifies two outputs that would land on one path.
/// Selected with <c>--collision-naming</c>.
/// </summary>
/// <remarks>
/// StyleCop SA1649 compares the file name up to its first dot with the first type in the
/// file. So the order of the two name parts decides whether the qualified file passes.
/// The choice changes planned paths, so every phase of one run must be given the same value.
/// </remarks>
internal enum CollisionNaming
{
    /// <summary>
    /// <c>OriginalFileName.TypeName.cs</c>, for example <c>A.Shared.cs</c>. The default.
    /// SA1649 reads the name as <c>A</c>, so the file violates SA1649.
    /// </summary>
    FileFirst,

    /// <summary>
    /// <c>TypeName.OriginalFileName.cs</c>, for example <c>Shared.A.cs</c>. SA1649 reads
    /// the name as <c>Shared</c>, so the file passes.
    /// </summary>
    TypeFirst,
}
