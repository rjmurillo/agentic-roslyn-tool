namespace AgenticRoslynTool;

/// <summary>
/// Refuses a planned row whose new file lands on a path another input owns: another row's
/// renamed kept file, another row's new file, or an input that stays where it is.
/// </summary>
/// <remarks>
/// <para>
/// The per-file plan probes the disk, so it cannot see a path that another input in the
/// same run is about to create. The name collision pass in
/// <see cref="SplitPlanner.ResolvePlannedOutputCollisions"/> compares new files with new
/// files only. A renamed kept file was never an entry there. So one input could rename its
/// kept file to <c>A.cs</c> while another planned a new <c>A.cs</c>. Both rows were
/// <c>split</c>, the rename landed, and the content phase skipped the second row with
/// <c>target path already exists</c>. AutoMapper's <c>src/UnitTests/Bug</c> folder hit it.
/// </para>
/// <para>
/// The new file's owner is refused, never the rename. Qualifying the new file would not
/// help: the content phase probes the computed simple name as well as the manifest path,
/// and the rename puts a file on that simple name. AGENTS.md rule 10 forbids exempting
/// either probe. Dropping the rename instead makes the outcome depend on input order. See
/// decision 32.
/// </para>
/// <para>
/// Two inputs that rename to one target are not refused here.
/// <see cref="KeptRenameCollisions"/> gives the rename to the first and keeps the other in
/// place. Paths are compared ignoring case, like every other collision check in the plan.
/// A row's own paths never refuse it, because a moved type may land on the path its own
/// kept file vacates.
/// </para>
/// </remarks>
internal static class PlannedOutputClaims
{
    /// <summary>The skip reason for a row whose new file another input already claims.</summary>
    internal const string Reason = "planned output path collision with another input requires manual handling";

    /// <summary>
    /// Runs before names are qualified. Refuses a row whose computed new file path is
    /// another row's rename target.
    /// </summary>
    /// <remarks>
    /// This runs on the computed paths because the content phase probes them. A qualified
    /// name would still meet the renamed file on its computed simple name. Refusing first
    /// also means the refused row no longer forces its partners into a qualified name.
    /// </remarks>
    /// <param name="results">One row per input, with new files at their computed paths.</param>
    internal static IReadOnlyList<FileResult> RefuseRenameTargets(IReadOnlyList<FileResult> results)
    {
        var claims = results
            .Where(r => r.Status == "split" && r.GitMove)
            .Select(r => (Path: r.KeptPath, Owner: r.OriginalPath))
            .ToArray();
        return Refuse(results, claims);
    }

    /// <summary>
    /// Runs after names are qualified. Refuses a row whose new file is a path that any other
    /// row creates, renames into, or leaves in place. Repeats until no row changes, because
    /// a refused row that planned a rename now stays at its original path.
    /// </summary>
    /// <param name="results">One row per input, with every collision name already chosen.</param>
    internal static IReadOnlyList<FileResult> RefuseSharedOutputs(IReadOnlyList<FileResult> results)
    {
        while (true)
        {
            var claims = results.SelectMany(Claims).ToArray();
            var refused = Refuse(results, claims);
            if (ReferenceEquals(refused, results))
            {
                return results;
            }

            results = refused;
        }
    }

    /// <summary>Every path one row will occupy once all three phases have run.</summary>
    private static IEnumerable<(string Path, string Owner)> Claims(FileResult row)
    {
        if (row.Status != "split")
        {
            return [(row.OriginalPath, row.OriginalPath)];
        }

        return row.NewFiles.Select(f => (f.Path, row.OriginalPath)).Prepend((row.KeptPath, row.OriginalPath));
    }

    /// <summary>
    /// Turns each <c>split</c> row whose new file another owner claims into a skip. Returns
    /// <paramref name="results"/> itself when no row is refused, so a plan with no such
    /// collision is unchanged.
    /// </summary>
    private static IReadOnlyList<FileResult> Refuse(IReadOnlyList<FileResult> results, IReadOnlyList<(string Path, string Owner)> claims)
    {
        var ownersByPath = claims.ToLookup(c => c.Path, c => c.Owner, StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var rows = results.Select(row =>
        {
            if (row.Status != "split")
            {
                return row;
            }

            var conflicts = row.NewFiles
                .SelectMany(f => ownersByPath[f.Path]
                    .Where(owner => !PathComparison.Comparer.Equals(owner, row.OriginalPath))
                    .Select(owner => $"output {f.Path} is also claimed by {owner}"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (conflicts.Length == 0)
            {
                return row;
            }

            changed = true;
            return FileResult.Skip(row.OriginalPath, row.OriginalPath, Reason) with { Note = string.Join("; ", conflicts), ContentHash = row.ContentHash };
        }).ToArray();
        return changed ? rows : results;
    }
}
