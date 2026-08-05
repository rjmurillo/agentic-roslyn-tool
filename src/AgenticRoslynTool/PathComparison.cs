namespace AgenticRoslynTool;

/// <summary>
/// Decides when two path strings name the same file.
/// </summary>
/// <remarks>
/// <para>
/// Used for identity only: de-duplicating the input list, keying the plan manifest
/// during the content phase, and deciding whether two planned outputs came from one
/// source file or two. Those have to agree, or a path that survives de-duplication can
/// be looked up against a different file's plan.
/// </para>
/// <para>
/// Safety checks stay on <see cref="StringComparer.OrdinalIgnoreCase"/> on purpose.
/// Deciding that two output paths collide when they do not costs a skipped file;
/// deciding that they do not collide when they do costs an overwrite. The conservative
/// answer is the ignore-case one, so collision detection does not use this comparer.
/// </para>
/// </remarks>
internal static class PathComparison
{
    /// <summary>
    /// Path equality matching how the running platform's filesystem conventionally treats
    /// case. A fixed case-insensitive comparison collapses <c>Foo.cs</c> and <c>foo.cs</c>
    /// on Linux, where they are two separate files.
    /// </summary>
    /// <remarks>
    /// This is an operating-system default, not a per-volume answer. A case-sensitive
    /// volume mounted on Windows or macOS is still compared case-insensitively here, which
    /// can collapse two genuinely distinct files into one input. Probing the volume would
    /// mean a filesystem write per run, so the default stands and the limitation is stated.
    /// </remarks>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
