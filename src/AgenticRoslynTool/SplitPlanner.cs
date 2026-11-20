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
            .SelectMany(r => r.NewFiles.Select(f => new PlannedOutputEntry(r.OriginalPath, f.Type, f.Path, CollisionNames.GetQualifiedPath(r.OriginalPath, f.Path, collisionNaming))))
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
        var refusedNames = collisionNaming != CollisionNaming.TypeFirst
            ? new Dictionary<string, string>(PathComparison.Comparer)
            : resolvedPaths
                .Where(kvp => CollisionNames.HasGeneratedCodeName(kvp.Value))
                .GroupBy(kvp => kvp.Key.OriginalPath, kvp => kvp.Value, PathComparison.Comparer)
                .ToDictionary(g => g.Key, g => string.Join("; ", g.Order(StringComparer.Ordinal)), PathComparison.Comparer);
        var notesBySource = collisionGroups
            .SelectMany(group => group.Select(entry => new
            {
                entry.OriginalPath,
                Note = "resolved output path collision: " + string.Join("; ", group
                    .Where(e => !refusedNames.ContainsKey(e.OriginalPath))
                    .OrderBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase)
                    .Select(e => CollisionNames.FormatResolution(e.OriginalPath, resolvedPaths[(e.OriginalPath, e.Type, e.Path)]))),
            }))
            .GroupBy(x => x.OriginalPath, PathComparison.Comparer)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(x => x.Note).Distinct(StringComparer.Ordinal)), PathComparison.Comparer);

        return results.Select(result =>
        {
            if (qualifiedCollisions.Contains(result.OriginalPath))
            {
                return FileResult.Skip(result.OriginalPath, result.KeptPath, "qualified output path collision requires manual handling") with { ContentHash = result.ContentHash };
            }

            if (refusedNames.TryGetValue(result.OriginalPath, out var refused))
            {
                return FileResult.Skip(result.OriginalPath, result.KeptPath, CollisionNames.GeneratedNameReason) with { Note = "refused output name: " + refused, ContentHash = result.ContentHash };
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

    /// <summary>
    /// Indexes plan rows by every path a later phase can be handed for them: the original
    /// path, and the kept path of a row whose rename is planned. The content phase passes
    /// the <c>split</c> rows to find what it can act on, and every row to find the hash a
    /// report row repeats. The renames phase passes every row, because it holds a file to
    /// whatever the plan recorded for it.
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
    internal static Dictionary<string, FileResult> IndexByInputPath(IEnumerable<FileResult> planRows)
    {
        var rows = planRows.ToArray();
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
    /// True when an earlier content run already applied this planned row. A retry reads the
    /// same plan again, so it meets the rows that succeeded the first time, and they must be
    /// a skip the caller can tell apart from a refusal.
    /// </summary>
    /// <remarks>
    /// Both halves are required. The file read must still declare a type while declaring
    /// none of the types the plan moved out, and every planned output must exist. A file
    /// that still declares a planned type is not applied work. The one caller asks only
    /// about a file whose bytes no longer match the plan, and a false answer there leaves
    /// the file a stale plan refusal. This method only ever picks the reason for a skip. It
    /// grants no exemption from any check, and nothing is written on any path through it.
    /// The check does not compare the types that remain against the plan, so a source
    /// edited after planning can read as applied.
    /// </remarks>
    /// <param name="planned">The manifest row for the file.</param>
    /// <param name="types">The top level types the file declares now.</param>
    internal static bool IsAlreadyApplied(FileResult planned, IReadOnlyList<TopLevelType> types)
    {
        if (planned.NewFiles.Count == 0 || types.Count == 0)
        {
            return false;
        }

        var movedTypes = planned.NewFiles.Select(f => f.Type).ToHashSet(StringComparer.Ordinal);
        return !types.Any(t => movedTypes.Contains(t.Key)) && planned.NewFiles.All(f => File.Exists(f.Path));
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
    /// not ask this run for. The same holds for a manifest planned under the other
    /// <c>--file-naming</c> value.
    /// <para>
    /// An input that declares a <c>file</c> type is planned over its ordinary types only.
    /// The <c>file</c> types stay in the input file, which is never renamed, and a moved
    /// type that names one refuses the input. <see cref="FileLocalTypes"/> makes each of
    /// those decisions.
    /// </para>
    /// </remarks>
    /// <param name="originalPath">The input path the plan row is keyed by.</param>
    /// <param name="readPath">The path the source is read from, which is the kept path once a rename has landed.</param>
    /// <param name="types">Every top-level type in the input, in source order.</param>
    /// <param name="planned">The plan manifest row for this input in the content phase; null while planning.</param>
    /// <param name="directiveNote">A note from directive analysis to carry onto the plan.</param>
    /// <param name="collisionNaming">Which qualified form a cross-file collision name takes.</param>
    /// <param name="convention">
    /// The SA1649 file naming convention. It decides both which type is kept and what each
    /// file is called, so it has no default: a caller that forgot it would plan
    /// <c>stylecop</c> names inside a <c>metadata</c> run.
    /// </param>
    /// <returns>A <see cref="SplitPlan"/>; a non-null <see cref="SplitPlan.SkipReason"/> means the split was refused.</returns>
    internal static SplitPlan BuildPlan(string originalPath, string readPath, IReadOnlyList<TopLevelType> types, FileResult? planned, string? directiveNote, CollisionNaming collisionNaming, FileNamingConvention convention)
    {
        var directory = Path.GetDirectoryName(readPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(originalPath);
        // A file-local type is never kept by choice and never moved. It stays in the input
        // file whatever the plan decides, so the plan is made over the ordinary types.
        var fileLocal = types.Where(t => t.HasFileModifier).ToArray();
        var candidates = fileLocal.Length == 0 ? types : types.Where(t => !t.HasFileModifier).ToArray();
        if (fileLocal.Length != 0 && candidates.Count < 2)
        {
            // FileLocalTypes.DescribeRefusal refuses this input first. Asked again here so
            // this method never picks a kept type from an empty list.
            return new SplitPlan(types[0], originalPath, false, null, FileLocalTypes.NothingToSplitReason, Array.Empty<PlannedFile>());
        }

        var (keep, keepOwnsFileName) = ChooseKept(candidates, baseName, convention);
        var gitMove = false;
        string? note = null;
        var keptPath = originalPath;

        if (fileLocal.Length != 0)
        {
            // No rename, in any phase: the compiler builds a file-local type's metadata
            // name from the file's name, so a rename is a behavior change. The kept path
            // stays the input path and gitMove stays false.
            (keep, note, var dependency) = FileLocalTypes.PlanAround(keep, keepOwnsFileName, baseName, candidates, fileLocal);
            if (dependency is not null)
            {
                return new SplitPlan(keep, keptPath, gitMove, null, FileLocalTypes.MovedTypeReason, Array.Empty<PlannedFile>(), SkipNote: dependency);
            }
        }
        else if (!keepOwnsFileName)
        {
            var target = Path.Combine(directory, OutputBuilder.GetFileName(keep, types, convention));
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
                note = KeptRenameCollisions.DescribeDroppedRename(target);
            }
        }

        var paths = new Dictionary<string, TopLevelType>(StringComparer.OrdinalIgnoreCase);
        paths[keptPath] = keep;
        foreach (var type in candidates.Where(t => !ReferenceEquals(t, keep)))
        {
            // A part with the kept type's name and type parameters belongs in the kept file.
            // GetFileName alone misses this when the kept file keeps its original name: the
            // kept Q<T> stays in Q.cs while a second part of Q<T> is named Q{T}.cs.
            var target = StringComparer.Ordinal.Equals(type.Key, keep.Key)
                ? keptPath
                : Path.Combine(directory, OutputBuilder.GetFileName(type, types, convention));
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
            // The name each moved type would get under the other --file-naming value. The
            // content phase uses it to recognize a plan made under that value.
            var other = convention == FileNamingConvention.Metadata ? FileNamingConvention.StyleCop : FileNamingConvention.Metadata;
            var otherNames = files.Where(f => !f.IsKept).ToDictionary(f => f.Type.Key, f => Path.Combine(directory, OutputBuilder.GetFileName(f.Type, types, other)), StringComparer.Ordinal);
            var (substituted, refusal, refusalNote) = ApplyManifestPaths(originalPath, keptPath, gitMove, files, planned, collisionNaming, convention, otherNames);
            if (refusal is not null)
            {
                return new SplitPlan(keep, keptPath, gitMove, note, refusal, Array.Empty<PlannedFile>(), SkipNote: refusalNote);
            }

            files = substituted;
        }

        note = CombineNotes(note, directiveNote);
        return new SplitPlan(keep, keptPath, gitMove, note, null, files);
    }


    /// <summary>
    /// Content phase only. Swaps each moved type's computed path for the path the manifest
    /// planned, then checks the result against the disk and against the manifest row.
    /// </summary>
    /// <returns>The substituted files, or a refusal reason with an optional note for the skipped row.</returns>
    private static (PlannedFile[] Files, string? Refusal, string? RefusalNote) ApplyManifestPaths(string originalPath, string keptPath, bool gitMove, PlannedFile[] computed, FileResult planned, CollisionNaming collisionNaming, FileNamingConvention convention, IReadOnlyDictionary<string, string> otherConventionPaths)
    {
        var plannedByType = planned.NewFiles.ToDictionary(f => f.Type, f => f.Path, StringComparer.Ordinal);
        var computedPaths = computed.Where(f => !f.IsKept).ToDictionary(f => f.Type.Key, f => f.Path, StringComparer.Ordinal);
        var files = computed
            .Select(file => !file.IsKept && plannedByType.TryGetValue(file.Type.Key, out var plannedPath) ? file with { Path = plannedPath } : file)
            .ToArray();
        var moved = files.Where(f => !f.IsKept).ToArray();

        // The path written is the substituted one, so it needs its own existence check.
        // No originalPath or readPath exemption here: a legitimate planned path never
        // equals either, so exempting them only lets an edited manifest aim a write at
        // the file being read.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keptPath };
        foreach (var file in moved)
        {
            if (File.Exists(file.Path))
            {
                return (files, $"target path already exists: {file.Path}", null);
            }

            if (!claimed.Add(file.Path))
            {
                return (files, $"target path collision within split: {file.Path}", null);
            }
        }

        // The collision pass does not run in this phase, so the manifest is the only place
        // a collision name can come from. One the plan phase resolved under the other
        // --collision-naming value is a plan this run was not asked to apply. This runs
        // after the checks above so those keep their own, more specific reasons.
        foreach (var file in moved)
        {
            var mismatch = CollisionNames.DescribeOtherNamingForm(originalPath, computedPaths[file.Type.Key], file.Path, planned.Note, collisionNaming)
                ?? PlanMismatch.DescribeOtherConventionForm(originalPath, computedPaths[file.Type.Key], otherConventionPaths[file.Type.Key], file.Path, convention);
            if (mismatch is not null)
            {
                return (files, PlanMismatchReason, mismatch);
            }
        }

        var plannedNewFiles = planned.NewFiles.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var actualNewFiles = moved.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var matches = StringComparer.OrdinalIgnoreCase.Equals(planned.KeptPath, keptPath)
            && planned.GitMove == gitMove
            && plannedNewFiles.SequenceEqual(actualNewFiles, StringComparer.OrdinalIgnoreCase);
        return matches ? (files, null, null) : (files, PlanMismatchReason, PlanMismatch.DescribeShape(planned, keptPath, gitMove, actualNewFiles));
    }

    /// <summary>
    /// Picks the type that stays in the input file, and says whether the file is already
    /// named for it. When it is not, the caller plans a rename.
    /// </summary>
    /// <remarks>
    /// Under <c>stylecop</c> the kept type is the one whose simple name matches the file,
    /// the non-generic one first, and that match is also what makes the name correct.
    /// Under <c>metadata</c> the file name carries the arity, so <c>Foo`1.cs</c> belongs to
    /// <c>Foo&lt;T&gt;</c>. Matching on the simple name alone would never find it and would
    /// move <c>Foo&lt;T&gt;</c> out of its own correctly named file. The simple name match
    /// stays as the fallback for choosing, but a generic type chosen that way sits in a
    /// plain <c>Foo.cs</c>, which SA1649 rejects under this convention, so the file does
    /// not count as named for it.
    /// </remarks>
    private static (TopLevelType Keep, bool OwnsFileName) ChooseKept(IReadOnlyList<TopLevelType> types, string baseName, FileNamingConvention convention)
    {
        var bySimpleName = types
            .Where(t => StringComparer.OrdinalIgnoreCase.Equals(t.Name, baseName))
            .OrderBy(t => t.TypeParameters.Count == 0 ? 0 : 1)
            .FirstOrDefault() ?? types[0];
        if (convention != FileNamingConvention.Metadata)
        {
            return (bySimpleName, StringComparer.OrdinalIgnoreCase.Equals(bySimpleName.Name, baseName));
        }

        var byMetadataName = types.FirstOrDefault(t => StringComparer.OrdinalIgnoreCase.Equals(OutputBuilder.GetMetadataName(t), baseName));
        return byMetadataName is null ? (bySimpleName, false) : (byMetadataName, true);
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
