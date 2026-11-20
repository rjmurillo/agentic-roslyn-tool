namespace AgenticRoslynTool;

/// <summary>
/// Recognizes a manifest path made under the other <c>--file-naming</c> value, and says
/// why a content phase row disagrees with the plan manifest it was handed. Everything here
/// is pure over its arguments.
/// </summary>
/// <remarks>
/// Split out of <see cref="SplitPlanner"/>, as <see cref="CollisionNames"/> was. The planner
/// runs the checks in order and refuses the row. This type knows what the other convention's
/// name looks like and words the note. The text goes into the skipped row's note. That row replaces the plan row, so the note is the only place the manifest's
/// value survives.
/// </remarks>
internal static class PlanMismatch
{
    /// <summary>
    /// Describes a manifest path that is the name the other <c>--file-naming</c> value
    /// gives this type, or returns null when the path is not one.
    /// </summary>
    /// <remarks>
    /// The content phase writes the manifest's paths, so nothing else notices that a plan
    /// was made under the other convention. Only that one name is refused, alone or in
    /// either cross-file qualified form. Any other free path a reviewer typed into the
    /// manifest is still applied, as it was before the flag existed. A type that is not
    /// generic has one name under both conventions and is never refused here.
    /// A manifest row carries no record of the convention, so a reviewer who types the
    /// other convention's exact name by hand is refused too. Nothing tells the two apart.
    /// </remarks>
    /// <param name="originalPath">The input path the plan row is keyed by.</param>
    /// <param name="computedPath">The path this run computes for the type.</param>
    /// <param name="otherPath">The path the other convention computes for the type.</param>
    /// <param name="plannedPath">The path the manifest holds for the type.</param>
    /// <param name="convention">The convention this run was given.</param>
    /// <returns>A note for the skipped row, or null when the path is not the other convention's.</returns>
    internal static string? DescribeOtherConventionForm(string originalPath, string computedPath, string otherPath, string plannedPath, FileNamingConvention convention)
    {
        // This run's own form is never a mismatch, and it is checked first. That also covers
        // a type that is not generic, whose two names are one string.
        if (IsPathOrQualifiedForm(originalPath, computedPath, plannedPath) || !IsPathOrQualifiedForm(originalPath, otherPath, plannedPath))
        {
            return null;
        }

        var (used, planned) = convention == FileNamingConvention.Metadata ? ("metadata", "stylecop") : ("stylecop", "metadata");
        return $"manifest path {plannedPath} is the --file-naming {planned} name and this run used {used}, which computes {computedPath}; run the plan phase again and pass one value to every phase";
    }

    private static bool IsPathOrQualifiedForm(string originalPath, string path, string candidate) =>
        StringComparer.OrdinalIgnoreCase.Equals(candidate, path)
        || StringComparer.OrdinalIgnoreCase.Equals(candidate, CollisionNames.GetQualifiedPath(originalPath, path, CollisionNaming.FileFirst))
        || StringComparer.OrdinalIgnoreCase.Equals(candidate, CollisionNames.GetQualifiedPath(originalPath, path, CollisionNaming.TypeFirst));

    /// <summary>
    /// Says what differs between the manifest row and this run's plan shape, which is the
    /// kept path, the rename flag, and the set of new file paths.
    /// </summary>
    /// <remarks>
    /// The text goes into the skipped row's note. It names the manifest's value wherever the
    /// manifest has one, because that row replaces the plan row and records this run's kept
    /// path. A path only this run plans has no manifest value, so that text names the run's
    /// path. The caller checks uniqueness of the run's paths first, and the last branch
    /// relies on that.
    /// </remarks>
    /// <param name="planned">The plan manifest row for this input.</param>
    /// <param name="keptPath">The kept path this run computed.</param>
    /// <param name="gitMove">Whether this run plans a rename of the kept file.</param>
    /// <param name="actualNewFiles">This run's new file paths, after manifest paths were applied.</param>
    /// <returns>What differs, or null when nothing here explains the mismatch.</returns>
    internal static string? DescribeShape(FileResult planned, string keptPath, bool gitMove, IReadOnlyList<string> actualNewFiles)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(planned.KeptPath, keptPath))
        {
            return $"manifest keeps {planned.KeptPath}, this run keeps {keptPath}; if --file-naming changed since the plan, run the plan phase again and pass one value to every phase";
        }

        // The kept paths agree here, and the row carries that path, so the text names none.
        if (planned.GitMove != gitMove)
        {
            return $"manifest {DescribeRename(planned.GitMove)}, this run {DescribeRename(gitMove)}";
        }

        var plannedNewFiles = planned.NewFiles.Select(f => f.Path).ToArray();
        if (plannedNewFiles.Except(actualNewFiles, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } onlyInManifest)
        {
            return $"manifest has {onlyInManifest}, which this run does not plan";
        }

        if (actualNewFiles.Except(plannedNewFiles, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } onlyInRun)
        {
            return $"this run plans {onlyInRun}, which the manifest lacks";
        }

        // With equal sets and distinct run paths, a longer manifest list repeats a path.
        var repeated = plannedNewFiles.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return repeated is null ? null : $"manifest lists {repeated.Key} more than once";
    }

    private static string DescribeRename(bool gitMove) => gitMove ? "renames the kept file" : "plans no rename";
}
