namespace AgenticRoslynTool;

/// <summary>
/// Decides what a split would do, without doing it: which type stays in the original
/// file, where each moved type lands, which inputs collide with each other, and which
/// path a phase should read from.
/// </summary>
/// <remarks>
/// Split out of <see cref="FileSplitter"/> because the plan is the tool's contract with
/// the agent driving it, and it was previously interleaved with the code that applies the
/// plan. Everything here is static and reads no instance state. Two of these methods do
/// probe the file system: <see cref="BuildPlan"/> and <see cref="GetReadPath"/> both call
/// <c>File.Exists</c> on a candidate target, which is what makes the plan-then-apply
/// workflow safe. The rest are pure over their arguments.
/// </remarks>
internal static class SplitPlanner
{
    private const string PlanMismatchReason = "content phase plan does not match manifest";

    /// <summary>The skip reason for a type-first name that compilers and analyzers read as generated code.</summary>
    internal const string GeneratedNameReason = "qualified output name would be read as generated code; manual handling required";

    /// <summary>
    /// File name endings, before the extension, that Roslyn treats as generated code. Taken
    /// from <c>GeneratedCodeUtilities.IsGeneratedCodeFile</c> in dotnet/roslyn.
    /// </summary>
    private static readonly string[] GeneratedCodeSuffixes = [".designer", ".generated", ".g", ".g.i"];

    /// <summary>
    /// Cross-file collision pass. If two different source files would each emit an
    /// output file with the same simple name (for example both emitting <c>Foo.cs</c>),
    /// each is qualified in the form <paramref name="collisionNaming"/> selects. If the
    /// qualified form still collides, the affected inputs are converted to skips with a
    /// manual-handling reason.
    /// </summary>
    /// <remarks>
    /// The default form, <c>OriginalFileName.TypeName.cs</c>, violates StyleCop SA1649
    /// (file name must match first type). The tool prefers a StyleCop violation over a
    /// build break; the alternative would be to refuse to split, which loses coverage.
    /// <see cref="CollisionNaming.TypeFirst"/> swaps the two parts so SA1649 passes. It is
    /// opt-in because it changes planned paths, and a plan and its content run must agree.
    /// This pass does not probe the disk for the qualified path. The content phase does,
    /// in <see cref="BuildPlan"/>, before anything is written.
    /// </remarks>
    /// <param name="results">One row per input, as the plan phase computed them.</param>
    /// <param name="collisionNaming">Which qualified form to use for a colliding output.</param>
    internal static IReadOnlyList<FileResult> ResolvePlannedOutputCollisions(IReadOnlyList<FileResult> results, CollisionNaming collisionNaming = CollisionNaming.FileFirst)
    {
        var entries = results
            .Where(r => r.Status == "split")
            .SelectMany(r => r.NewFiles.Select(f => new PlannedOutputEntry(r.OriginalPath, f.Type, f.Path, GetQualifiedPath(r.OriginalPath, f.Path, collisionNaming))))
            .ToArray();
        // Two comparers, on purpose. Grouping by output path asks "could these two writes
        // hit the same file", which stays ignore-case so a same-name-different-case pair is
        // treated as a collision. Counting distinct sources asks "are these two different
        // files", which is identity and must follow the platform: on Linux, Foo.cs and
        // foo.cs really are two inputs, and folding them into one hides a real collision.
        var collisionGroups = entries
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(e => e.OriginalPath).Distinct(PathComparison.Comparer).Count() > 1)
            .ToArray();

        if (collisionGroups.Length == 0)
        {
            return results;
        }

        var resolvedPaths = collisionGroups
            .SelectMany(g => g)
            .ToDictionary(e => (e.OriginalPath, e.Type, e.Path), e => e.QualifiedPath);
        var qualifiedCollisions = entries
            .Where(e => resolvedPaths.ContainsKey((e.OriginalPath, e.Type, e.Path)))
            .GroupBy(e => e.QualifiedPath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(e => e.OriginalPath))
            .ToHashSet(PathComparison.Comparer);

        // Type-first ends the name with the source file's base name. A source called G.cs
        // or Designer.cs would yield Shared.G.cs, which Roslyn reads as generated code:
        // analyzers skip it and nullable annotations switch off. That is a behavior change,
        // so the input is handed back. File-first is left alone to keep its plans identical.
        var generatedNames = collisionNaming != CollisionNaming.TypeFirst
            ? new HashSet<string>(PathComparison.Comparer)
            : resolvedPaths
                .Where(kvp => HasGeneratedCodeName(kvp.Value))
                .Select(kvp => kvp.Key.OriginalPath)
                .ToHashSet(PathComparison.Comparer);
        var notesBySource = collisionGroups
            .SelectMany(group => group.Select(entry => new
            {
                entry.OriginalPath,
                Note = "resolved output path collision: " + string.Join("; ", group
                    .OrderBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase)
                    .Select(e => $"{e.OriginalPath} -> {resolvedPaths[(e.OriginalPath, e.Type, e.Path)]}")),
            }))
            .GroupBy(x => x.OriginalPath, PathComparison.Comparer)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(x => x.Note).Distinct(StringComparer.Ordinal)), PathComparison.Comparer);

        return results.Select(result =>
        {
            if (qualifiedCollisions.Contains(result.OriginalPath))
            {
                return FileResult.Skip(result.OriginalPath, result.KeptPath, "qualified output path collision requires manual handling");
            }

            if (generatedNames.Contains(result.OriginalPath))
            {
                return FileResult.Skip(result.OriginalPath, result.KeptPath, GeneratedNameReason);
            }

            var newFiles = result.NewFiles.Select(file =>
            {
                var key = (result.OriginalPath, file.Type, file.Path);
                return resolvedPaths.TryGetValue(key, out var resolvedPath) ? file with { Path = resolvedPath } : file;
            }).ToArray();
            var note = result.Note;
            if (notesBySource.TryGetValue(result.OriginalPath, out var collisionNote))
            {
                note = string.IsNullOrEmpty(note) ? collisionNote : note + " | " + collisionNote;
            }

            return result with { NewFiles = newFiles, Note = note };
        }).ToArray();
    }

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

    private static bool HasGeneratedCodeName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return GeneratedCodeSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Describes a manifest path that is the qualified form of the collision naming this run
    /// was not given, or returns null when the path is not that form.
    /// </summary>
    /// <remarks>
    /// The content phase takes output paths from the manifest and never reruns the collision
    /// pass, so nothing else notices that a plan was made under the other
    /// <c>--collision-naming</c> value. The test is this narrow on purpose. Requiring every
    /// manifest path to be one the run would compute would refuse any hand-edited path, which
    /// the content phase has always accepted when the path is free. The two forms of one
    /// output are never equal: a moved type never shares the source file's base name, because
    /// the type that does is the one kept in place.
    /// </remarks>
    private static string? DescribeOtherCollisionNamingForm(string originalPath, string computedPath, string plannedPath, CollisionNaming collisionNaming)
    {
        var other = collisionNaming == CollisionNaming.FileFirst ? CollisionNaming.TypeFirst : CollisionNaming.FileFirst;
        if (!StringComparer.OrdinalIgnoreCase.Equals(plannedPath, GetQualifiedPath(originalPath, computedPath, other)))
        {
            return null;
        }

        return $"manifest path {plannedPath} is the --collision-naming {FlagValue(other)} form and this run used {FlagValue(collisionNaming)}; run the plan phase again and pass one value to every phase";
    }

    private static string FlagValue(CollisionNaming collisionNaming)
    {
        return collisionNaming == CollisionNaming.TypeFirst ? "type-first" : "file-first";
    }

    /// <summary>
    /// Indexes the plan's <c>split</c> rows by every path the content phase can be handed
    /// for them: the original path, and the kept path of a row whose rename is planned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A directory input is scanned again in each phase. After the renames phase, that scan
    /// finds a renamed file under its kept path, which the plan never listed as an input.
    /// Without the kept path key, the content phase skips the file, reports its plan row as
    /// not supplied, and exits 0 with the split undone.
    /// </para>
    /// <para>
    /// Only the lookup changes. The row found here still runs through
    /// <see cref="GetReadPath"/> and <see cref="BuildPlan"/>, so every rename state and
    /// manifest check applies exactly as it does to a row found by its original path. An
    /// original path wins over a kept path, and a kept path that two rows claim maps to
    /// neither, so this index never guesses which plan a file belongs to.
    /// </para>
    /// </remarks>
    internal static Dictionary<string, FileResult> IndexByInputPath(IEnumerable<FileResult> splitRows)
    {
        var rows = splitRows.ToArray();
        var index = rows.ToDictionary(r => r.OriginalPath, PathComparison.Comparer);
        var renamed = rows
            .Where(r => r.GitMove)
            .GroupBy(r => r.KeptPath, PathComparison.Comparer)
            .Where(g => g.Count() == 1 && !index.ContainsKey(g.Key))
            .ToArray();
        foreach (var group in renamed)
        {
            index.Add(group.Key, group.Single());
        }

        return index;
    }

    /// <summary>
    /// Chooses which path to read source from. In the content phase for a row whose
    /// rename was already applied, the original path no longer exists and the kept
    /// path is authoritative. This method enforces that state: it refuses to proceed
    /// if the renames phase was skipped, or if both paths exist (an inconsistent
    /// rename state that would silently write to the wrong file).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the expected renamed file is missing or if both the original and renamed paths still exist.</exception>
    internal static string GetReadPath(string originalPath, Phase phase, FileResult? planned)
    {
        if (phase != Phase.Content || planned is null || !planned.GitMove)
        {
            return originalPath;
        }

        if (!File.Exists(planned.KeptPath))
        {
            throw new InvalidOperationException($"Content phase expected renamed file at {planned.KeptPath}. Run --phase renames and commit before --phase content.");
        }

        if (File.Exists(originalPath))
        {
            throw new InvalidOperationException($"Content phase found both original and renamed paths for {originalPath}. Resolve rename state before continuing.");
        }

        return planned.KeptPath;
    }


    /// <summary>
    /// Builds the split plan for one input file: picks which type is kept in place,
    /// chooses target paths for the types being moved out, and (in the content phase)
    /// reconciles the recomputed plan against the manifest row supplied by the plan
    /// phase.
    /// </summary>
    /// <remarks>
    /// Two of the refusals here are load-bearing. The tool refuses when a target path
    /// already exists on disk (except when it happens to be the current input file),
    /// and refuses when the recomputed plan does not match the manifest handed to the
    /// content phase. Together these are what make the plan-then-apply workflow safe.
    /// The mismatch refusal also covers a manifest planned under the other
    /// <c>--collision-naming</c> value, so a content run cannot apply names the caller did
    /// not ask this run for.
    /// </remarks>
    /// <returns>A <see cref="SplitPlan"/>; a non-null <see cref="SplitPlan.SkipReason"/> means the split was refused.</returns>
    internal static SplitPlan BuildPlan(string originalPath, string readPath, IReadOnlyList<TopLevelType> types, FileResult? planned, string? directiveNote, CollisionNaming collisionNaming)
    {
        var directory = Path.GetDirectoryName(readPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(originalPath);
        var matchingTypes = types.Where(t => StringComparer.OrdinalIgnoreCase.Equals(t.Name, baseName)).ToArray();
        var keep = matchingTypes.OrderBy(t => t.TypeParameters.Count == 0 ? 0 : 1).FirstOrDefault() ?? types[0];
        var gitMove = false;
        string? note = null;
        var keptPath = originalPath;

        if (!StringComparer.OrdinalIgnoreCase.Equals(keep.Name, baseName))
        {
            var target = Path.Combine(directory, OutputBuilder.GetFileName(keep, types));
            if (StringComparer.OrdinalIgnoreCase.Equals(target, readPath))
            {
                keptPath = target;
                gitMove = true;
            }
            else if (!StringComparer.OrdinalIgnoreCase.Equals(target, originalPath) && !File.Exists(target))
            {
                keptPath = target;
                gitMove = true;
            }
            else
            {
                note = $"rename collision for {target}; kept original file name";
            }
        }

        var paths = new Dictionary<string, TopLevelType>(StringComparer.OrdinalIgnoreCase);
        paths[keptPath] = keep;
        foreach (var type in types.Where(t => !ReferenceEquals(t, keep)))
        {
            // A part with the kept type's name and type parameters belongs in the kept file.
            // GetFileName alone misses this when the kept file keeps its original name: the
            // kept Q<T> stays in Q.cs while a second part of Q<T> is named Q{T}.cs.
            var target = StringComparer.Ordinal.Equals(type.Key, keep.Key)
                ? keptPath
                : Path.Combine(directory, OutputBuilder.GetFileName(type, types));
            if (paths.ContainsKey(target))
            {
                return new SplitPlan(keep, keptPath, gitMove, note, $"target path collision within split: {target}", Array.Empty<PlannedFile>());
            }

            if (File.Exists(target)
                && !StringComparer.OrdinalIgnoreCase.Equals(target, originalPath)
                && !StringComparer.OrdinalIgnoreCase.Equals(target, readPath))
            {
                return new SplitPlan(keep, keptPath, gitMove, note, $"target path already exists: {target}", Array.Empty<PlannedFile>());
            }

            paths[target] = type;
        }

        var files = paths.Select(kvp => new PlannedFile(kvp.Key, kvp.Value, ReferenceEquals(kvp.Value, keep))).ToArray();
        if (planned is not null)
        {
            var plannedByType = planned.NewFiles.ToDictionary(f => f.Type, f => f.Path, StringComparer.Ordinal);
            var computedPaths = files.Where(f => !f.IsKept).ToDictionary(f => f.Type.Key, f => f.Path, StringComparer.Ordinal);
            files = files.Select(file =>
            {
                if (file.IsKept)
                {
                    return file;
                }

                return plannedByType.TryGetValue(file.Type.Key, out var plannedPath) ? file with { Path = plannedPath } : file;
            }).ToArray();

            // The path written is the substituted one, so it needs its own existence check.
            // No originalPath or readPath exemption here: a legitimate planned path never
            // equals either, so exempting them only lets an edited manifest aim a write at
            // the file being read.
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keptPath };
            foreach (var file in files.Where(f => !f.IsKept))
            {
                if (File.Exists(file.Path))
                {
                    return new SplitPlan(keep, keptPath, gitMove, note, $"target path already exists: {file.Path}", Array.Empty<PlannedFile>());
                }

                if (!claimed.Add(file.Path))
                {
                    return new SplitPlan(keep, keptPath, gitMove, note, $"target path collision within split: {file.Path}", Array.Empty<PlannedFile>());
                }
            }

            // The collision pass does not run in this phase, so the manifest is the only place
            // a collision name can come from. One built under the other --collision-naming
            // value is a plan this run was not asked to apply. This runs after the existence
            // and uniqueness checks so those keep their own, more specific reasons.
            foreach (var file in files.Where(f => !f.IsKept))
            {
                var mismatch = DescribeOtherCollisionNamingForm(originalPath, computedPaths[file.Type.Key], file.Path, collisionNaming);
                if (mismatch is not null)
                {
                    return new SplitPlan(keep, keptPath, gitMove, note, PlanMismatchReason, Array.Empty<PlannedFile>(), SkipNote: mismatch);
                }
            }

            var plannedNewFiles = planned.NewFiles.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var actualNewFiles = files.Where(f => !f.IsKept).Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!StringComparer.OrdinalIgnoreCase.Equals(planned.KeptPath, keptPath) || planned.GitMove != gitMove || !plannedNewFiles.SequenceEqual(actualNewFiles, StringComparer.OrdinalIgnoreCase))
            {
                return new SplitPlan(keep, keptPath, gitMove, note, PlanMismatchReason, Array.Empty<PlannedFile>());
            }
        }

        note = CombineNotes(note, directiveNote);
        return new SplitPlan(keep, keptPath, gitMove, note, null, files);
    }


    internal static string? CombineNotes(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first))
        {
            return second;
        }

        return string.IsNullOrEmpty(second) ? first : first + " | " + second;
    }

}
