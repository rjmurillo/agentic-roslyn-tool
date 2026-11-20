namespace AgenticRoslynTool;

/// <summary>Binds the rows a later phase reports to the plan manifest it read. A run that starts a plan
/// records content hashes. The renames and content phases only carry and compare them, and
/// this type is where both phases do the carrying.</summary>
/// <remarks>Kept out of <see cref="FileSplitter"/> because it decides and never acts: a row goes in,
/// and the row the phase may act on and report comes out. It writes nothing, and it probes
/// the file system once, to learn whether a planned rename has landed.
/// Only the plan phase writes the manifest. These rules were written when every phase wrote
/// its rows back over it, and a row carrying a hash the phase computed would then have
/// matched on the next run. They stay, so no reported row ever shows a hash the plan phase
/// did not record.</remarks>
internal static class PlanBinding
{
    /// <summary>Reason recorded when the renames phase would split a file the manifest on disk has no row for.</summary>
    private const string NoPlanRowReason = ContentHash.StalePlanPrefix + "the plan manifest has no row for this file; re-run --phase plan";

    /// <summary>Reason recorded when the renames phase would split a file the manifest on disk records as skipped or failed.</summary>
    private const string NotPlannedAsSplitReason = ContentHash.StalePlanPrefix + "the plan manifest does not mark this file as split; re-run --phase plan";

    /// <summary>Binds one freshly computed renames row to the manifest already on disk.</summary>
    /// <param name="row">The row the renames phase just computed for one input.</param>
    /// <param name="priorPlan">The manifest rows, indexed by <see cref="SplitPlanner.IndexByInputPath"/>.</param>
    /// <returns>The row to act on and to report, which is a stale plan refusal when the file may not be touched.</returns>
    /// <remarks>Three rules, and each closes a path the others leave open.
    /// <list type="number">
    /// <item>The row always takes its hash from the manifest row, never from the bytes just
    /// read, so this phase cannot replace or invent a recorded hash whatever the row's
    /// status turns out to be.</item>
    /// <item>A file this run read whose bytes differ from a recorded hash is refused as
    /// stale whatever the recomputed row says, so a changed file is reported as changed and
    /// not under some unrelated skip reason.</item>
    /// <item>A row that would act, a <c>split</c>, is refused unless the manifest marks that
    /// file <c>split</c>.</item>
    /// </list>
    /// A refusal keeps the recorded hash, so it stays a refusal on every later run for as
    /// long as the file differs from the plan. Only the plan phase starts a new plan.</remarks>
    internal static FileResult HoldRenamesRow(FileResult row, Dictionary<string, FileResult> priorPlan)
    {
        var wouldAct = row.Status == "split";

        // The rename has not happened, so a refused file is still at the path it was read from.
        if (!priorPlan.TryGetValue(row.OriginalPath, out var prior) || IsStrayAtKeptPath(row, prior))
        {
            return wouldAct
                ? FileResult.StalePlan(row.OriginalPath, row.OriginalPath, NoPlanRowReason, null)
                : row with { ContentHash = null };
        }

        // A null hash on the fresh row means this run never read the file, so there are no
        // bytes to compare. A null hash on the recorded row means no plan read it either.
        // That only refuses a row that would act: a file nobody planned, which this run
        // would leave alone anyway, has nothing to be stale against.
        if (row.ContentHash is not null && (prior.ContentHash is not null || wouldAct))
        {
            var changed = ContentHash.DescribeMismatch(prior.ContentHash, row.ContentHash);
            if (changed is not null)
            {
                return FileResult.StalePlan(row.OriginalPath, row.OriginalPath, changed, prior.ContentHash);
            }
        }

        return wouldAct && prior.Status != "split"
            ? FileResult.StalePlan(row.OriginalPath, row.OriginalPath, NotPlannedAsSplitReason, prior.ContentHash)
            : row with { ContentHash = prior.ContentHash };
    }

    /// <summary>
    /// True when <paramref name="row"/> reached <paramref name="prior"/> through its kept
    /// path while the file the plan read is still at its original path.
    /// </summary>
    /// <remarks>
    /// The kept path stands for a planned file only after the rename moved it there. While
    /// the original still exists, whatever sits at the kept path is some other file, and it
    /// has no row. A copy has the same bytes, so the hash cannot tell the two apart. The
    /// content phase makes the same call in <see cref="SplitPlanner.GetReadPath"/>.
    /// </remarks>
    private static bool IsStrayAtKeptPath(FileResult row, FileResult prior) =>
        !PathComparison.Comparer.Equals(row.OriginalPath, prior.OriginalPath) && File.Exists(prior.OriginalPath);

    /// <summary>Restores, on a content phase row, the two facts about the plan that the refusal and failure factories drop: the pending-rename flag and the content hash.</summary>
    /// <param name="result">The row the content phase produced for one input.</param>
    /// <param name="planned">The manifest row for that input, or null in the plan and renames phases.</param>
    /// <returns>The row with the planned hash, and for a row that is not a split, the planned rename flag.</returns>
    /// <remarks>Whether a rename is owed is a fact about the plan, not about how this run ended, so a
    /// row that forgets it tells the next reader no move is pending for a file that still
    /// needs one. The paths are deliberately left alone: they say where the file is, which a
    /// planned row cannot answer for a run that refused before the rename landed.
    /// The hash is the same kind of fact. It is carried on every row the content phase
    /// reports for a planned file, including the exits that never read the file: an excluded
    /// path, a missing file, an inconsistent rename state. Carried on every status, so no
    /// reported row shows a hash other than the planned one.</remarks>
    internal static FileResult CarryPlannedFacts(FileResult result, FileResult? planned)
    {
        if (planned is null)
        {
            return result;
        }

        var carried = result with { ContentHash = planned.ContentHash };
        return result.Status == "split" ? carried : carried with { GitMove = planned.GitMove };
    }
}
