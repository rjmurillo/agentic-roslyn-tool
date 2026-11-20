namespace AgenticRoslynTool;

/// <summary>
/// Decides which planned kept-file renames stand. When two inputs plan to rename their kept
/// file to one path, the first keeps the rename and each later one keeps its original name.
/// In the renames phase a rename also has to be the one the reviewed plan holds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SplitPlanner.BuildPlan"/> plans one input at a time and probes the disk, so it
/// drops a rename whose target already exists. It cannot see a target that another input in
/// the same run is about to create. Left alone, the renames phase moved the first file and
/// <c>git mv</c> failed on the second, so the plan promised a rename that could not land.
/// <c>--file-naming metadata</c> made that easy to reach: a generic type split across
/// <c>Foo.cs</c> and <c>Foo.Extra.cs</c> plans <c>Foo`1.cs</c> twice.
/// </para>
/// <para>
/// The later row gets the outcome and the note <see cref="SplitPlanner.BuildPlan"/> gives a
/// rename whose target exists. That is what the content phase computes for it once the
/// first rename has landed, so the two agree from then on. Until that rename lands the
/// content phase still computes the rename and skips the row as a plan mismatch. That
/// includes a winner the renames phase refuses, or one that is gone: the later row keeps
/// the plan's answer through <see cref="HoldToPlannedRename"/>, and both wait for a new plan.
/// </para>
/// <para>
/// Two things keep the agreement. Only a row that is still <c>split</c> claims a target, so
/// this pass runs after every pass that can refuse a row. And targets are compared with
/// <see cref="PathComparison.Comparer"/>, because the content phase decides the same
/// question with a disk probe. On a case-sensitive disk <c>Foo.cs</c> and <c>foo.cs</c> are
/// two targets, and both renames stand.
/// </para>
/// </remarks>
internal static class KeptRenameCollisions
{
    /// <summary>Drops every planned rename whose target an earlier input in the run already claimed.</summary>
    /// <param name="results">One row per input, in input order, after every pass that can refuse a row.</param>
    /// <returns>The rows, with each later claimant of a rename target left at its original path.</returns>
    internal static IReadOnlyList<FileResult> Resolve(IReadOnlyList<FileResult> results)
    {
        var claimed = new HashSet<string>(PathComparison.Comparer);
        return results.Select(result =>
        {
            if (result.Status != "split" || !result.GitMove || claimed.Add(result.KeptPath))
            {
                return result;
            }

            var note = SplitPlanner.CombineNotes(result.Note, DescribeDroppedRename(result.KeptPath));
            return result with { KeptPath = result.OriginalPath, GitMove = false, Note = note };
        }).ToArray();
    }

    /// <summary>
    /// Renames phase only. Keeps a recomputed rename only when the reviewed plan asks for
    /// that same rename, and drops any other.
    /// </summary>
    /// <remarks>
    /// The renames phase recomputes the plan, so it can arrive at a rename the manifest row
    /// does not hold: one where the plan has none, or one to another target. Three things
    /// cause it. The row lost its target to an earlier input in the plan, which happens on
    /// every ordinary run of two inputs with one target. That earlier input has since been
    /// refused or removed. Or the run was given another <c>--file-naming</c>. In each case
    /// the content phase reads the kept path the manifest holds, so a rename made here
    /// would strand the file. The row stays where the plan left it.
    /// A row the plan dropped for a collision keeps the plan's own note. Any other row is
    /// told the rename is not in the plan.
    /// </remarks>
    /// <param name="row">A renames phase row, already held to the manifest's hash and status.</param>
    /// <param name="priorPlan">The plan manifest rows, indexed by original path and by planned kept path.</param>
    /// <returns>The row, without its rename unless the manifest row plans that rename.</returns>
    internal static FileResult HoldToPlannedRename(FileResult row, Dictionary<string, FileResult> priorPlan)
    {
        if (row.Status != "split" || !row.GitMove || !priorPlan.TryGetValue(row.OriginalPath, out var planned))
        {
            return row;
        }

        if (planned.GitMove && PathComparison.Comparer.Equals(row.KeptPath, planned.KeptPath))
        {
            return row;
        }

        var collision = DescribeDroppedRename(row.KeptPath);
        var plannedAsCollision = planned.Note?.Contains(collision, StringComparison.Ordinal) == true;
        var dropped = plannedAsCollision ? collision : $"rename to {row.KeptPath} is not in the plan; kept original file name";
        return row with { KeptPath = row.OriginalPath, GitMove = false, Note = SplitPlanner.CombineNotes(row.Note, dropped) };
    }

    /// <summary>The note for a planned rename that was dropped because its target is taken, on disk or by an earlier input.</summary>
    internal static string DescribeDroppedRename(string target) => $"rename collision for {target}; kept original file name";
}
