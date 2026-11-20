namespace AgenticRoslynTool;

/// <summary>
/// Cross-file pass over planned kept-file renames. When two inputs plan to rename their kept
/// file to one path, the first keeps the rename and each later one keeps its original name.
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
/// includes a winner the renames phase refuses as stale: the later row keeps the plan's
/// answer through <see cref="DropUnplannedRename"/>, and both wait for a new plan.
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
    /// Renames phase only. Drops a rename the reviewed plan did not ask for.
    /// </summary>
    /// <remarks>
    /// The renames phase recomputes the plan, so it can arrive at a rename the manifest row
    /// records as none. Two things cause it. The row lost its target to another input in
    /// the plan and that input has since been refused, or the run was given another
    /// <c>--file-naming</c>. Either way the content phase reads the path the manifest
    /// kept, so a rename made here would strand the file. The row stays as planned.
    /// </remarks>
    /// <param name="row">A renames phase row, already held to the manifest's hash and status.</param>
    /// <param name="priorPlan">The plan manifest rows, indexed by input path.</param>
    /// <returns>The row, without its rename when the manifest row plans none.</returns>
    internal static FileResult DropUnplannedRename(FileResult row, Dictionary<string, FileResult> priorPlan)
    {
        if (row.Status != "split" || !row.GitMove || !priorPlan.TryGetValue(row.OriginalPath, out var planned) || planned.GitMove)
        {
            return row;
        }

        var note = SplitPlanner.CombineNotes(row.Note, $"rename to {row.KeptPath} is not in the plan; kept original file name");
        return row with { KeptPath = row.OriginalPath, GitMove = false, Note = note };
    }

    /// <summary>The note for a planned rename that was dropped because its target is taken, on disk or by an earlier input.</summary>
    internal static string DescribeDroppedRename(string target) => $"rename collision for {target}; kept original file name";
}
