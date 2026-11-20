namespace AgenticRoslynTool;

/// <summary>
/// The <c>--collision-naming</c> value as text, and the rule that picks a run's value from the
/// flag and the manifest. <see cref="CollisionNames"/> builds the paths each value produces.
/// </summary>
internal static class CollisionNamingFlag
{
    private static readonly CollisionNaming[] Values = [CollisionNaming.FileFirst, CollisionNaming.TypeFirst];

    /// <summary>The one spelling of a value, used by the flag, the manifest column, and the JSON report.</summary>
    internal static string Format(CollisionNaming collisionNaming) => collisionNaming == CollisionNaming.TypeFirst ? "type-first" : "file-first";

    /// <summary>Parses a <see cref="Format"/> spelling, ignoring case.</summary>
    /// <param name="value">The text from the command line or the manifest.</param>
    /// <param name="collisionNaming">The parsed value, or the default when parsing fails.</param>
    /// <returns>True when <paramref name="value"/> is <c>file-first</c> or <c>type-first</c>.</returns>
    internal static bool TryParse(string value, out CollisionNaming collisionNaming)
    {
        var index = Array.FindIndex(Values, candidate => StringComparer.OrdinalIgnoreCase.Equals(value, Format(candidate)));
        collisionNaming = index < 0 ? default : Values[index];
        return index >= 0;
    }

    /// <summary>
    /// Chooses the collision naming a renames or content run uses, from the flag and from the
    /// value the plan recorded in the manifest.
    /// </summary>
    /// <remarks>
    /// The recorded value wins when the flag is absent, so a caller does not have to repeat the
    /// flag on every phase. A flag that contradicts the recorded value ends the run before any
    /// file is touched. Skipping the affected rows instead would exit 0, which reads as a run
    /// with nothing to do.
    /// </remarks>
    /// <param name="given">The <c>--collision-naming</c> value, or null when the flag was absent.</param>
    /// <param name="recorded">The manifest's value, or null when there is no manifest or it has no rows.</param>
    /// <param name="manifestPath">The manifest path, named in the error.</param>
    /// <exception cref="InvalidOperationException">Thrown when both values are present and differ.</exception>
    internal static CollisionNaming Resolve(CollisionNaming? given, CollisionNaming? recorded, string manifestPath)
    {
        if (given is { } flag && recorded is { } plan && flag != plan)
        {
            throw new InvalidOperationException(
                $"--collision-naming {Format(flag)} contradicts the plan manifest, which was planned with {Format(plan)}. Omit the flag to use the plan's value, or re-run --phase plan with the new value: {manifestPath}");
        }

        return recorded ?? given ?? CollisionNaming.FileFirst;
    }
}
