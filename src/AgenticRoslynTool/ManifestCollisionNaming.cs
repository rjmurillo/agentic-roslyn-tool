namespace AgenticRoslynTool;

/// <summary>
/// The manifest's <c>collisionNaming</c> column: its header, its spelling, and the strict
/// read that renames and content rely on. See decision 25.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="ManifestWriter"/>, which turns rows into CSV and back. This
/// type reads one run level value and refuses a manifest that does not carry it cleanly.
/// </remarks>
internal static class ManifestCollisionNaming
{
    /// <summary>The header of the column that records the run's <c>--collision-naming</c> value.</summary>
    internal const string Column = "collisionNaming";

    /// <summary>The cell text for <paramref name="collisionNaming"/>, the same spelling the flag uses.</summary>
    internal static string Format(CollisionNaming collisionNaming) => CollisionNamingFlag.Format(collisionNaming);

    /// <summary>Reads the collision naming a plan recorded in its <c>collisionNaming</c> column.</summary>
    /// <remarks>
    /// The value belongs to the whole run, but CSV has no place for a file level value, so
    /// <see cref="ManifestWriter.ToCsv"/> repeats it on every row. Every row must therefore agree. A blank,
    /// unknown, or mixed value means someone edited the file, and guessing would pick
    /// output paths nobody reviewed, so each of those ends the run.
    /// Call it only after <see cref="ManifestWriter.Read"/> with <c>requireContentHash</c>, so a manifest from
    /// an older version reports the <c>contentHash</c> error first. That error carries the
    /// same advice, re-run the plan, and is the one older manifests have always produced.
    /// </remarks>
    /// <param name="path">Absolute path to a manifest produced by <see cref="ManifestWriter.Write"/>.</param>
    /// <returns>The recorded value, or null when the manifest has no data rows.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the column is missing, or any row's value is blank, unknown, or differs from another row's.</exception>
    public static CollisionNaming? Read(string path)
    {
        using var parser = new CsvFieldReader(path);
        var index = Array.FindIndex(parser.ReadFields() ?? [], name => StringComparer.OrdinalIgnoreCase.Equals(name, Column));
        if (index < 0)
        {
            throw Refuse($"Plan manifest has no {Column} column, so the run cannot tell which --collision-naming the plan used. An older version wrote it.", path);
        }

        // Counts data records, not file lines: a quoted note can span several lines.
        CollisionNaming? recorded = null;
        var row = 0;
        while (!parser.EndOfData)
        {
            if (parser.ReadFields() is not { Length: > 0 } fields)
            {
                continue;
            }

            row++;
            var value = index < fields.Length ? fields[index] : string.Empty;
            var parsed = CollisionNamingFlag.TryParse(value, out var named)
                ? named
                : throw Refuse($"Plan manifest data row {row} has {Column} '{value}'. Expected file-first or type-first on every row.", path);
            recorded = recorded is not { } first || first == parsed
                ? parsed
                : throw Refuse($"Plan manifest mixes {Column} values: data row {row} has {Format(parsed)} and an earlier row has {Format(first)}.", path);
        }

        return recorded;
    }

    // Every refusal gives the same advice, so it is written once.
    private static InvalidOperationException Refuse(string problem, string path) =>
        new($"{problem} Re-run --phase plan to rebuild it: {path}");
}
