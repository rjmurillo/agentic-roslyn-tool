namespace AgenticRoslynTool;

/// <summary>
/// Decides what a split would do, without doing it: which type stays in the original
/// file, where each moved type lands, which inputs collide with each other, and which
/// path a phase should read from.
/// </summary>
/// <remarks>
/// Split out of <see cref="FileSplitter"/> because the plan is the tool's contract with
/// the agent driving it, and it was previously interleaved with the code that applies the
/// plan. Everything here is static and reads only its arguments, so a plan can be built
/// and asserted on without a run.
/// </remarks>
internal static class SplitPlanner
{
    /// <summary>
    /// Cross-file collision pass. If two different source files would each emit an
    /// output file with the same simple name (for example both emitting <c>Foo.cs</c>),
    /// each is qualified as <c>OriginalFileName.TypeName.cs</c>. If the qualified form
    /// still collides, the affected inputs are converted to skips with a manual-handling
    /// reason.
    /// </summary>
    /// <remarks>
    /// The qualified fallback form violates StyleCop SA1649 (file name must match
    /// first type). The tool prefers a StyleCop violation over a build break; the
    /// alternative would be to refuse to split, which loses coverage.
    /// </remarks>
    internal static IReadOnlyList<FileResult> ResolvePlannedOutputCollisions(IReadOnlyList<FileResult> results)
    {
        var entries = results
            .Where(r => r.Status == "split")
            .SelectMany(r => r.NewFiles.Select(f => new PlannedOutputEntry(r.OriginalPath, f.Type, f.Path, GetQualifiedPath(r.OriginalPath, f.Path))))
            .ToArray();
        var collisionGroups = entries
            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(e => e.OriginalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
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
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notesBySource = collisionGroups
            .SelectMany(group => group.Select(entry => new
            {
                entry.OriginalPath,
                Note = "resolved output path collision: " + string.Join("; ", group
                    .OrderBy(e => e.OriginalPath, StringComparer.OrdinalIgnoreCase)
                    .Select(e => $"{e.OriginalPath} -> {resolvedPaths[(e.OriginalPath, e.Type, e.Path)]}")),
            }))
            .GroupBy(x => x.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(x => x.Note).Distinct(StringComparer.Ordinal)), StringComparer.OrdinalIgnoreCase);

        return results.Select(result =>
        {
            if (qualifiedCollisions.Contains(result.OriginalPath))
            {
                return FileResult.Skip(result.OriginalPath, result.KeptPath, "qualified output path collision requires manual handling");
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
    internal static string GetQualifiedPath(string originalPath, string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? ".";
        var sourceBaseName = Path.GetFileNameWithoutExtension(originalPath);
        var targetFileName = Path.GetFileName(targetPath);
        return Path.Combine(directory, sourceBaseName + "." + targetFileName);
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
    /// </remarks>
    /// <returns>A <see cref="SplitPlan"/>; a non-null <see cref="SplitPlan.SkipReason"/> means the split was refused.</returns>
    internal static SplitPlan BuildPlan(string originalPath, string readPath, IReadOnlyList<TopLevelType> types, FileResult? planned, string? directiveNote)
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
            var target = Path.Combine(directory, OutputBuilder.GetFileName(type, types));
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

            var plannedNewFiles = planned.NewFiles.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var actualNewFiles = files.Where(f => !f.IsKept).Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!StringComparer.OrdinalIgnoreCase.Equals(planned.KeptPath, keptPath) || planned.GitMove != gitMove || !plannedNewFiles.SequenceEqual(actualNewFiles, StringComparer.OrdinalIgnoreCase))
            {
                return new SplitPlan(keep, keptPath, gitMove, note, "content phase plan does not match manifest", Array.Empty<PlannedFile>());
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
