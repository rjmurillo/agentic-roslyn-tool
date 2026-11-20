namespace AgenticRoslynTool;

/// <summary>
/// Builds and recognizes the qualified names the cross-file collision pass gives two
/// outputs that would land on one path. Everything here is pure over its arguments.
/// </summary>
/// <remarks>
/// Split out of <see cref="SplitPlanner"/> so the planner keeps deciding and this type
/// keeps naming. The plan phase writes a name through <see cref="GetQualifiedPath"/> and
/// <see cref="FormatResolution"/>. The content phase reads it back through
/// <see cref="DescribeOtherNamingForm"/>. Keeping both ends in one type is what stops the
/// written text and the text looked for from drifting apart.
/// </remarks>
internal static class CollisionNames
{
    /// <summary>The skip reason for a type-first name that compilers and analyzers read as generated code.</summary>
    internal const string GeneratedNameReason = "qualified output name would be read as generated code; manual handling required";

    // Taken from GeneratedCodeUtilities.IsGeneratedCodeFile in dotnet/roslyn.

    /// <summary>File name endings, before the extension, that Roslyn treats as generated code.</summary>
    private static readonly string[] GeneratedCodeSuffixes = [".designer", ".generated", ".g", ".g.i"];

    /// <summary>Builds the qualified fallback path used to break same-simple-name collisions across two different source files.</summary>
    /// <remarks>
    /// Both forms are built from the target file name the plan already chose, never from the
    /// type again. So whatever naming produced <paramref name="targetPath"/>, such as
    /// <c>Foo{T}.cs</c>, carries through: type-first gives <c>Foo{T}.A.cs</c>.
    /// </remarks>
    /// <param name="originalPath">The input file the output is carved from.</param>
    /// <param name="targetPath">The simple-name output path that collided.</param>
    /// <param name="collisionNaming">Which of the two name parts comes first.</param>
    internal static string GetQualifiedPath(string originalPath, string targetPath, CollisionNaming collisionNaming)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? ".";
        var sourceBaseName = Path.GetFileNameWithoutExtension(originalPath);
        var targetFileName = Path.GetFileName(targetPath);
        if (collisionNaming == CollisionNaming.TypeFirst)
        {
            return Path.Combine(directory, Path.GetFileNameWithoutExtension(targetFileName) + "." + sourceBaseName + Path.GetExtension(targetFileName));
        }

        return Path.Combine(directory, sourceBaseName + "." + targetFileName);
    }

    /// <summary>True when Roslyn would treat a file at <paramref name="path"/> as generated code, going by its name alone.</summary>
    internal static bool HasGeneratedCodeName(string path) =>
        GeneratedCodeSuffixes.Any(suffix => Path.GetFileNameWithoutExtension(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Describes a manifest path that the plan phase resolved under the collision naming
    /// this run was not given, or returns null when the path is not one.
    /// </summary>
    /// <remarks>
    /// The content phase takes output paths from the manifest and never reruns the collision
    /// pass, so nothing else notices that a plan was made under the other
    /// <c>--collision-naming</c> value.
    /// Two things must both hold. The path has the other value's qualified form, and the
    /// row's note records that the collision pass chose that path. The note is what tells a
    /// plan made under the other value from a path a reviewer typed into the manifest. A
    /// hand-edited free path, of any shape, is accepted exactly as it was before the flag
    /// existed.
    /// The two forms can be one string. <c>A.A.cs</c> moving a type <c>A</c> gives
    /// <c>A.A.A.cs</c> either way. A path that is also this run's own form is never a
    /// mismatch, so that case is excluded first.
    /// </remarks>
    internal static string? DescribeOtherNamingForm(string originalPath, string computedPath, string plannedPath, string? plannedNote, CollisionNaming collisionNaming)
    {
        var other = collisionNaming == CollisionNaming.FileFirst ? CollisionNaming.TypeFirst : CollisionNaming.FileFirst;
        var comparer = StringComparer.OrdinalIgnoreCase;
        if (comparer.Equals(plannedPath, GetQualifiedPath(originalPath, computedPath, collisionNaming))
            || !comparer.Equals(plannedPath, GetQualifiedPath(originalPath, computedPath, other))
            || plannedNote is null
            || !plannedNote.Contains(FormatResolution(originalPath, plannedPath), StringComparison.Ordinal))
        {
            return null;
        }

        return $"manifest path {plannedPath} is the --collision-naming {CollisionNamingFlag.Format(other)} form and this run used {CollisionNamingFlag.Format(collisionNaming)}; run the plan phase again, and do not edit the collisionNaming column";
    }

    /// <summary>The text the collision pass writes into a row's note for one resolved output.</summary>
    internal static string FormatResolution(string originalPath, string resolvedPath) => $"{originalPath} -> {resolvedPath}";
}
