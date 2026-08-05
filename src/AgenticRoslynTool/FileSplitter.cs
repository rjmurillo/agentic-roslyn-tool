using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AgenticRoslynTool;

/// <summary>
/// Core engine of the <c>split-types</c> command. Given a list of C# files that
/// declare more than one top-level type, it computes a per-file split plan, verifies
/// it against a set of correctness invariants, and (in the content phase) writes one
/// output file per top-level type. All parsing and rewriting go through the Roslyn
/// Syntax API; no semantic model is used.
/// </summary>
/// <remarks>
/// <para>
/// The workflow runs in three phases (see <see cref="Phase"/>): a plan phase emits a
/// CSV manifest and touches nothing, a renames phase performs the <c>git mv</c> calls
/// for files whose primary type does not match their file name, and a content phase
/// reads the plan manifest and performs the actual split. Splitting renames and
/// content into separate commits is what lets git record renames as renames rather
/// than as delete-plus-add pairs.
/// </para>
/// <para>
/// Using directives are deliberately never pruned. A using that looks unused can
/// still supply an extension method or a target-typed conversion, so removing it
/// would turn a mechanical edit into a semantic change. This is a decision, not an
/// oversight.
/// </para>
/// <para>
/// Verification runs entirely before any file is written: <c>VerifyOutputs</c>
/// executes before <c>WriteOutputs</c>. A file that fails verification is never
/// written and is recorded as <c>failed</c> in the manifest.
/// </para>
/// </remarks>
internal sealed class FileSplitter
{
    private readonly Options _options;

    /// <summary>
    /// Reason recorded when the content phase is handed a file the plan never covered. Named
    /// because the reason string decides whether the row reaches the manifest.
    /// </summary>
    private const string NotPlannedReason = "not present as split in plan manifest";

    /// <summary>Constructs a splitter bound to a set of parsed options.</summary>
    /// <param name="options">The options that select the input list, manifest path, repository root, phase, and optional required header.</param>
    public FileSplitter(Options options)
    {
        _options = options;
    }

    /// <summary>
    /// Runs the phase selected by <see cref="Options.Phase"/>.
    /// </summary>
    /// <returns>The manifest and report views of the run.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the phase value is unrecognized, or, in the content phase, if the plan manifest is missing.</exception>
    public RunOutcome Run()
    {
        return _options.Phase switch
        {
            Phase.Plan => RunOutcome.Same(RunPlan()),
            Phase.Renames => RunOutcome.Same(RunRenames()),
            Phase.Content => RunContent(),
            _ => throw new InvalidOperationException($"Unsupported phase: {_options.Phase}"),
        };
    }

    /// <summary>Plan phase entry point. Builds the resolved plan and returns it without writing anything.</summary>
    private IReadOnlyList<FileResult> RunPlan()
    {
        var results = BuildResolvedPlan();
        return results;
    }

    /// <summary>
    /// Renames phase entry point. Recomputes the plan and performs the <c>git mv</c>
    /// for every split row whose kept file name changes. Content edits are not
    /// performed in this phase, so the resulting commit contains only renames.
    /// </summary>
    private IReadOnlyList<FileResult> RunRenames()
    {
        var results = BuildResolvedPlan();
        foreach (var result in results.Where(r => r.Status == "split" && r.GitMove))
        {
            ApplyRenameOnly(result.OriginalPath, File.ReadAllBytes(result.OriginalPath), result.KeptPath);
        }

        return results;
    }

    /// <summary>Runs the plan for every input path and resolves any cross-file output-name collisions into their qualified form.</summary>
    private IReadOnlyList<FileResult> BuildResolvedPlan()
    {
        var results = new List<FileResult>();
        foreach (var path in ReadRunnableInputs(_options.InputPath).ToArray())
        {
            results.Add(Process(path, Phase.Plan, null));
        }

        return ResolvePlannedOutputCollisions(results);
    }

    /// <summary>
    /// Content phase entry point. Reads the plan manifest, then for each input path
    /// runs <see cref="Process"/> against the planned row. Files not present as
    /// <c>split</c> in the manifest are skipped with a diagnostic reason.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the manifest file does not exist.</exception>
    /// <remarks>
    /// The input list is materialized before the loop. Enumeration itself can fail, on an
    /// unreadable list file or a malformed path, and the content phase rewrites source as it
    /// goes. Streaming would let an early input be rewritten and a later one abort the run
    /// before the manifest is written, leaving changed files and no record of which. Per-file
    /// failures inside the loop are handled the same way for the same reason: <see cref="Process"/>
    /// turns any failure on one input, expected or not, into a row rather than an abort, so the
    /// manifest describes what the run actually did. That holds as far as the manifest write
    /// itself; a run whose <c>ManifestWriter.Write</c> fails still loses the record, and nothing
    /// inside this method can prevent that.
    /// </remarks>
    private RunOutcome RunContent()
    {
        if (!File.Exists(_options.ManifestPath))
        {
            throw new InvalidOperationException($"Content phase requires an existing plan manifest: {_options.ManifestPath}");
        }

        var inputs = ReadRunnableInputs(_options.InputPath).ToArray();

        // Read once and keep every row. The split-only view below decides what this run can
        // act on; the full set is what gets preserved, because a skipped or failed row is
        // part of the plan of record and a content run must not quietly drop it.
        //
        // PathComparison.Comparer, not OrdinalIgnoreCase: this keying has to agree with the
        // de-duplication in ReadRunnableInputs, or on Linux a plan row for Foo.cs could be
        // applied to foo.cs. A path carrying both a split row and a non-split row resolves
        // to the split one, so a duplicated manifest entry stays actionable.
        var manifestPlan = ManifestWriter.Read(_options.ManifestPath)
            .GroupBy(r => r.OriginalPath, PathComparison.Comparer)
            .Select(g => g.FirstOrDefault(r => r.Status == "split") ?? g.First())
            .ToArray();
        var plannedFiles = manifestPlan
            .Where(r => r.Status == "split")
            .ToDictionary(r => r.OriginalPath, PathComparison.Comparer);

        var results = new List<FileResult>();
        var applied = new HashSet<string>(PathComparison.Comparer);
        foreach (var path in inputs)
        {
            var originalPath = Path.GetFullPath(path);
            if (!plannedFiles.TryGetValue(originalPath, out var planned))
            {
                results.Add(FileResult.Skip(originalPath, originalPath, NotPlannedReason));
                continue;
            }

            applied.Add(originalPath);
            results.Add(Process(originalPath, Phase.Content, planned));
        }

        // The manifest is the plan of record; the report is what this run did. So the two
        // views diverge here.
        //
        // A planned row the current input never mentioned must survive the rewrite verbatim,
        // or applying content in batches would destroy the reviewed plan for every batch
        // after the first. That covers every row, not only the split ones: a skipped or
        // failed row carries the reason the plan phase refused that file, and losing it
        // means the next reader cannot tell a refused file from one never examined. Only
        // the split rows are reported as unapplied, because only they described work.
        //
        // An input the plan never covered is the mirror image: it belongs in the report so
        // the caller sees the mismatch, and not in the manifest, where it would accumulate a
        // permanent tail of rows the plan phase never produced.
        var unapplied = manifestPlan.Where(p => !applied.Contains(p.OriginalPath)).ToArray();
        var manifestRows = results
            .Where(r => !string.Equals(r.Reason, NotPlannedReason, StringComparison.Ordinal))
            .Concat(unapplied)
            .ToArray();
        var reportRows = results.Concat(unapplied
            .Where(p => p.Status == "split")
            .Select(p => FileResult.Skip(
                p.OriginalPath,
                p.KeptPath,
                "planned as split but not supplied to the content phase input"))).ToArray();

        return new RunOutcome(manifestRows, reportRows);
    }

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
    private static string GetQualifiedPath(string originalPath, string targetPath)
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
    private static string GetReadPath(string originalPath, Phase phase, FileResult? planned)
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
    /// Processes one input file through the requested phase: parses it, checks the
    /// preconditions that would make splitting unsafe, builds a plan, and (in the
    /// content phase) verifies and writes outputs. All refusals become <c>skipped</c>
    /// results with a diagnostic reason; unexpected exceptions become <c>failed</c>.
    /// </summary>
    /// <param name="path">Absolute or relative input path.</param>
    /// <param name="phase">The phase currently running.</param>
    /// <param name="planned">The manifest row for this input, or null in the plan and renames phases.</param>
    /// <returns>A result describing the outcome for this input.</returns>
    /// <remarks>
    /// The catch is unfiltered on purpose, and for the same reason the one in
    /// <c>Program.Main</c> is. The content phase rewrites files as it goes and writes the
    /// manifest at the end, so anything that escapes this method strands every file already
    /// rewritten with no record of them. A filtered catch only holds until some path throws a
    /// type nobody listed, and this method reaches an inconsistent rename state, a hand-edited
    /// manifest, and the file system. One bad input costs a failed row; one escaped exception
    /// costs the plan of record.
    /// </remarks>
    private FileResult Process(string path, Phase phase, FileResult? planned)
    {
        try
        {
            return ProcessCore(path, phase, planned);
        }
        catch (Exception ex)
        {
            // Path.GetFullPath throws on a malformed path. Every caller resolves the path
            // before handing it over, so this is idempotent today, but a guard of last resort
            // that can defeat itself on one line is not a guard.
            string reported;
            try
            {
                reported = Path.GetFullPath(path);
            }
            catch (Exception resolveFailure) when (resolveFailure is ArgumentException or PathTooLongException or NotSupportedException)
            {
                reported = path;
            }

            return FileResult.Failed(reported, reported, ex.Message);
        }
    }

    /// <summary>
    /// Implements <see cref="Process"/>. Kept separate so every exit path, including an
    /// unexpected throw, funnels through the one guard in the caller.
    /// </summary>
    /// <param name="path">Absolute or relative input path.</param>
    /// <param name="phase">The phase currently running.</param>
    /// <param name="planned">The manifest row for this input, or null in the plan and renames phases.</param>
    /// <returns>A result describing the outcome for this input.</returns>
    private FileResult ProcessCore(string path, Phase phase, FileResult? planned)
    {
        var originalPath = Path.GetFullPath(path);
        var excluded = MatchExclude(originalPath);
        if (excluded is not null)
        {
            return FileResult.Skip(originalPath, originalPath, $"excluded by pattern: {excluded}");
        }

        var readPath = GetReadPath(originalPath, phase, planned);
        if (!File.Exists(readPath))
        {
            return FileResult.Skip(originalPath, readPath, "input file does not exist");
        }

        byte[] originalBytes;
        try
        {
            originalBytes = File.ReadAllBytes(readPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // File.Exists said yes a moment ago, so this is a lock, a permission change, or a
            // disk error arriving mid-run. Same rule as the decode failure below: one bad file
            // becomes a row the caller can read, not an abort that strands every file already
            // rewritten in this run with no manifest recording them.
            return FileResult.Failed(originalPath, readPath, $"cannot read input: {ex.Message}");
        }

        EncodedSource source;
        try
        {
            source = EncodedSource.FromBytes(originalBytes);
        }
        catch (DecoderFallbackException ex)
        {
            // A file that does not decode cannot be round-tripped byte for byte, which is
            // the promise this tool makes. Skipping keeps one odd file from ending a run
            // that may already have rewritten hundreds of others.
            return FileResult.Skip(originalPath, readPath, $"input is not valid UTF-8 and has no byte order mark: {ex.Message}");
        }
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source.Text, source.Encoding), parseOptions, path: readPath);
        var root = tree.GetCompilationUnitRoot();
        var originalDiagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (originalDiagnostics.Length != 0)
        {
            return FileResult.Failed(originalPath, originalPath, "input has syntax errors: " + JoinDiagnostics(originalDiagnostics));
        }

        if (root.Members.OfType<GlobalStatementSyntax>().Any())
        {
            return FileResult.Skip(originalPath, originalPath, "contains top-level statements; manual split required");
        }

        var types = TopLevelType.Find(root).ToArray();
        if (types.Length <= 1)
        {
            return FileResult.Skip(originalPath, originalPath, $"nothing to split: input has {types.Length} top-level type declaration(s)");
        }

        if (types.Any(t => t.HasFileModifier))
        {
            return FileResult.Skip(originalPath, originalPath, "contains file-local type; manual split required");
        }

        var directiveSafety = AnalyzeDirectiveSafety(root, types);
        if (!directiveSafety.IsSafe)
        {
            return FileResult.Skip(originalPath, originalPath, directiveSafety.Reason ?? "contains unsafe directive; manual split required");
        }

        var plan = BuildPlan(originalPath, readPath, types, planned, directiveSafety.Note);
        if (plan.SkipReason is not null)
        {
            return FileResult.Skip(originalPath, plan.KeptPath, plan.SkipReason);
        }

        if (ProducesEmptyDirectiveShell(root, types, plan))
        {
            return FileResult.Skip(originalPath, plan.KeptPath, "splitting would leave an empty directive shell; manual split required");
        }

        try
        {
            if (phase == Phase.Renames)
            {
                ApplyRenameOnly(originalPath, originalBytes, plan.KeptPath);
                return FileResult.Split(
                    originalPath,
                    plan.KeptPath,
                    plan.GitMove,
                    plan.Note,
                    plan.Files.Where(f => !f.IsKept).Select(f => new NewFileResult(f.Path, f.Type.Key)).ToArray());
            }

            var headerText = NormalizeHeaderText(_options.RequiredHeader, source.NewLine);
            var outputs = BuildOutputs(source, root, types, plan, headerText);
            VerifyOutputs(readPath, types, outputs, parseOptions, headerText);

            if (phase == Phase.Content)
            {
                WriteOutputs(readPath, originalBytes, source, plan, outputs);
            }

            return FileResult.Split(
                originalPath,
                plan.KeptPath,
                plan.GitMove,
                plan.Note,
                outputs.Where(o => !StringComparer.OrdinalIgnoreCase.Equals(o.Path, plan.KeptPath))
                    .Select(o => new NewFileResult(o.Path, o.Type.Key))
                    .ToArray());
        }
        catch (Exception ex)
        {
            return FileResult.Failed(originalPath, plan.KeptPath, ex.Message);
        }
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
    private SplitPlan BuildPlan(string originalPath, string readPath, IReadOnlyList<TopLevelType> types, FileResult? planned, string? directiveNote)
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
            var target = Path.Combine(directory, GetFileName(keep, types));
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
            var target = Path.Combine(directory, GetFileName(type, types));
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

    /// <summary>
    /// Picks the output file name for one type. Two types with the same simple name
    /// in the same input are disambiguated by generic arity as
    /// <c>Name{T1,T2}.cs</c>. Two same-simple-name types in one input that do NOT
    /// differ by arity are caught earlier in <see cref="BuildPlan"/> and refused as
    /// <c>target path collision within split</c>.
    /// </summary>
    private static string GetFileName(TopLevelType type, IReadOnlyList<TopLevelType> allTypes)
    {
        var sameName = allTypes.Where(t => StringComparer.Ordinal.Equals(t.Name, type.Name)).ToArray();
        if (type.TypeParameters.Count > 0 && sameName.Length > 1)
        {
            return type.Name + "{" + string.Join(",", type.TypeParameters) + "}.cs";
        }

        return type.Name + ".cs";
    }

    /// <summary>
    /// Materializes the in-memory output text for every planned file. For each planned
    /// output, all other top-level types are removed with
    /// <c>SyntaxRemoveOptions.KeepUnbalancedDirectives</c> to prevent orphaned
    /// preprocessor tokens, assembly-level attributes are cleared on non-kept outputs
    /// (attributes stay only in the kept file), and the tree is run through
    /// <see cref="BlankLineCollapser"/> before serialization. The header is injected
    /// after body text is captured so line-conservation counting is not confused by
    /// it.
    /// </summary>
    private static OutputFile[] BuildOutputs(EncodedSource source, CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, SplitPlan plan, string headerText)
    {
        var header = headerText.Length == 0 ? string.Empty : headerText + source.NewLine + source.NewLine;
        var outputs = new List<OutputFile>();
        foreach (var file in plan.Files)
        {
            var remove = types.Where(t => !ReferenceEquals(t, file.Type)).Select(t => t.Node).ToArray();
            var newRoot = root.RemoveNodes(remove, SyntaxRemoveOptions.KeepUnbalancedDirectives) ?? root;
            if (!file.IsKept)
            {
                newRoot = newRoot.WithAttributeLists(default);
            }

            newRoot = (CompilationUnitSyntax)new BlankLineCollapser().Visit(newRoot)!;
            var rendered = newRoot.ToFullString();
            var body = EnsureTrailingNewLine(rendered, source.NewLine, source.HasFinalNewLine);
            var text = EnsureHeader(rendered, header);
            text = EnsureTrailingNewLine(text, source.NewLine, source.HasFinalNewLine);
            outputs.Add(new OutputFile(file.Path, file.Type, text, body));
        }

        return outputs.ToArray();
    }

    /// <summary>
    /// Refuses to split when any planned output would leave a preprocessor region
    /// (a <c>#region</c> or <c>#if</c>) containing only whitespace and nested
    /// directives. Prevents the visible artefact of an "empty shell" region that
    /// makes the split obviously wrong to a human reader even when it parses.
    /// </summary>
    private static bool ProducesEmptyDirectiveShell(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types, SplitPlan plan)
    {
        foreach (var file in plan.Files)
        {
            var remove = types.Where(t => !ReferenceEquals(t, file.Type)).Select(t => t.Node).ToArray();
            var newRoot = root.RemoveNodes(remove, SyntaxRemoveOptions.KeepUnbalancedDirectives) ?? root;
            if (ContainsEmptyDirectiveGroup(newRoot.ToFullString()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Scans one candidate output's rendered text for a preprocessor region that would be left containing only whitespace or nested directives.</summary>
    private static bool ContainsEmptyDirectiveGroup(string text)
    {
        var lines = SplitLineSegments(text).ToList();
        var stack = new Stack<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var directive = GetDirectiveKind(lines[i]);
            if (directive is "#region" or "#if")
            {
                stack.Push(i);
                continue;
            }

            if (directive is not "#endregion" and not "#endif")
            {
                continue;
            }

            if (stack.Count == 0)
            {
                continue;
            }

            var start = stack.Pop();
            if (HasOnlyWhitespaceOrDirectives(lines, start, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOnlyWhitespaceOrDirectives(IReadOnlyList<string> lines, int start, int end)
    {
        for (var i = start + 1; i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(TrimLineEnding(lines[i])) || GetDirectiveKind(lines[i]) is not null)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string? GetDirectiveKind(string line)
    {
        var trimmed = TrimLineEnding(line).TrimStart();
        foreach (var directive in new[] { "#region", "#endregion", "#if", "#elif", "#else", "#endif" })
        {
            if (trimmed.StartsWith(directive, StringComparison.Ordinal))
            {
                return directive;
            }
        }

        return null;
    }

    private static string TrimLineEnding(string line)
    {
        return line.TrimEnd('\r', '\n');
    }

    private static IEnumerable<string> SplitLineSegments(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
            {
                continue;
            }

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            yield return text[start..(i + 1)];
            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>
    /// Analyzes preprocessor directives (<c>#if/#elif/#else/#endif</c> and
    /// <c>#region/#endregion</c>) and refuses to split when any directive group spans
    /// more than one top-level type. Splitting an unbalanced directive group would
    /// leave dangling directives in at least one output.
    /// </summary>
    /// <returns>A <see cref="DirectiveSafety"/> whose <see cref="DirectiveSafety.IsSafe"/> is true only when every directive group is contained within a single top-level type.</returns>
    private static DirectiveSafety AnalyzeDirectiveSafety(CompilationUnitSyntax root, IReadOnlyList<TopLevelType> types)
    {
        var directives = root.DescendantTrivia(descendIntoTrivia: true)
            .Where(IsUnsafeDirective)
            .Select(trivia => DirectiveInfo.Create(root.SyntaxTree, trivia, FindOwningType(trivia.SpanStart, types)))
            .ToArray();
        if (directives.Length == 0)
        {
            return DirectiveSafety.Safe(null);
        }

        var regionStack = new Stack<List<DirectiveInfo>>();
        var ifStack = new Stack<List<DirectiveInfo>>();
        var groups = new List<List<DirectiveInfo>>();
        foreach (var directive in directives)
        {
            switch (directive.Trivia.Kind())
            {
                case SyntaxKind.RegionDirectiveTrivia:
                    regionStack.Push(new List<DirectiveInfo> { directive });
                    break;
                case SyntaxKind.EndRegionDirectiveTrivia:
                    if (regionStack.Count == 0)
                    {
                        return DirectiveSafety.Unsafe($"{directive.Kind} directive at line {directive.Line} has no matching #region");
                    }

                    var regionGroup = regionStack.Pop();
                    regionGroup.Add(directive);
                    groups.Add(regionGroup);
                    break;
                case SyntaxKind.IfDirectiveTrivia:
                    ifStack.Push(new List<DirectiveInfo> { directive });
                    break;
                case SyntaxKind.ElifDirectiveTrivia:
                case SyntaxKind.ElseDirectiveTrivia:
                    if (ifStack.Count == 0)
                    {
                        return DirectiveSafety.Unsafe($"{directive.Kind} directive at line {directive.Line} has no matching #if");
                    }

                    ifStack.Peek().Add(directive);
                    break;
                case SyntaxKind.EndIfDirectiveTrivia:
                    if (ifStack.Count == 0)
                    {
                        return DirectiveSafety.Unsafe($"{directive.Kind} directive at line {directive.Line} has no matching #if");
                    }

                    var ifGroup = ifStack.Pop();
                    ifGroup.Add(directive);
                    groups.Add(ifGroup);
                    break;
            }
        }

        if (regionStack.Count != 0)
        {
            var directive = regionStack.Peek()[0];
            return DirectiveSafety.Unsafe($"#region directive at line {directive.Line} has no matching #endregion");
        }

        if (ifStack.Count != 0)
        {
            var directive = ifStack.Peek()[0];
            return DirectiveSafety.Unsafe($"#if directive at line {directive.Line} has no matching #endif");
        }

        var owners = new Dictionary<int, TopLevelType>();
        foreach (var group in groups)
        {
            var owner = GetDirectiveGroupOwner(group, types);
            if (owner is null)
            {
                return DirectiveSafety.Unsafe($"{group[0].Kind} group at line {group[0].Line} crosses top-level type boundaries");
            }

            foreach (var directive in group)
            {
                owners[directive.Trivia.SpanStart] = owner;
            }
        }

        return DirectiveSafety.Safe("safe directives: " + string.Join("; ", directives.Select(d => $"{d.Kind} line {d.Line} -> {owners[d.Trivia.SpanStart].Key}")));
    }

    /// <summary>Finds the top-level type whose full span uniquely contains a position, or null when zero or more than one type qualifies.</summary>
    private static TopLevelType? FindOwningType(int position, IReadOnlyList<TopLevelType> types)
    {
        var matches = types.Where(type => type.Node.FullSpan.Start <= position && position < type.Node.FullSpan.End).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Resolves the single top-level type that a directive group belongs to. A group is
    /// considered owned when exactly one type overlaps its span, or when exactly one
    /// type is fully contained within it. Anything else is treated as crossing type
    /// boundaries and results in a split refusal.
    /// </summary>
    private static TopLevelType? GetDirectiveGroupOwner(IReadOnlyList<DirectiveInfo> group, IReadOnlyList<TopLevelType> types)
    {
        var groupStart = group.Min(d => d.Trivia.SpanStart);
        var groupEnd = group.Max(d => d.Trivia.Span.End);
        var overlappingTypes = types.Where(type => type.Node.SpanStart < groupEnd && groupStart < type.Node.Span.End).ToArray();
        if (overlappingTypes.Length == 1)
        {
            return overlappingTypes[0];
        }

        var containedTypes = types.Where(type => groupStart <= type.Node.SpanStart && type.Node.Span.End <= groupEnd).ToArray();
        return containedTypes.Length == 1 ? containedTypes[0] : null;
    }

    private static bool IsUnsafeDirective(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.IfDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.ElifDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.ElseDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.EndIfDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.RegionDirectiveTrivia)
            || trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia);
    }

    private static string? CombineNotes(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first))
        {
            return second;
        }

        return string.IsNullOrEmpty(second) ? first : first + " | " + second;
    }

    /// <summary>
    /// Normalizes a supplied required-header string to the source file's own newline
    /// style and strips any trailing newlines. The normalized value is used both for
    /// injection and for the <c>StartsWith</c> check in <see cref="VerifyOutputs"/>;
    /// they must stay in sync or a multi-line header supplied with <c>\n</c> against
    /// a CRLF source would fail verification and refuse to split.
    /// </summary>
    private static string NormalizeHeaderText(string? requiredHeader, string newLine)
    {
        if (string.IsNullOrWhiteSpace(requiredHeader))
        {
            return string.Empty;
        }

        return requiredHeader.Replace("\r\n", "\n").Replace("\n", newLine).TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Prepends the required header when the text does not already start with it.
    /// A file that already carries the banner keeps its existing spacing and does
    /// not receive a second copy.
    /// </summary>
    private static string EnsureHeader(string text, string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return text;
        }

        var headerProbe = header.TrimStart();
        if (headerProbe.Length == 0)
        {
            return text;
        }

        return text.TrimStart().StartsWith(headerProbe.TrimEnd(), StringComparison.Ordinal) ? text : header + text.TrimStart();
    }

    private static string EnsureTrailingNewLine(string text, string newLine, bool hasFinalNewLine)
    {
        var trimmed = text.TrimEnd('\r', '\n');
        return hasFinalNewLine ? trimmed + newLine : trimmed;
    }

    /// <summary>
    /// Runs every correctness check on the in-memory outputs before any file is
    /// written. The checks together protect these invariants:
    /// (1) every output starts with the required header when one was requested;
    /// (2) every output is a syntactically valid compilation unit;
    /// (3) each input type's declaration and owned trivia appears exactly once
    /// across the outputs (never dropped, never duplicated);
    /// (4) non-whitespace lines are conserved across the split
    /// (see <see cref="VerifyLineConservation"/>); and
    /// (5) the set of top-level types in the outputs matches the input set exactly.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown as soon as any invariant fails, so the failure is surfaced without partial output being written.</exception>
    private static void VerifyOutputs(string originalPath, IReadOnlyList<TopLevelType> inputTypes, IReadOnlyList<OutputFile> outputs, CSharpParseOptions parseOptions, string? requiredHeader)
    {
        foreach (var output in outputs)
        {
            if (!string.IsNullOrEmpty(requiredHeader) && !output.Text.StartsWith(requiredHeader, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{output.Path} does not start with required file header");
            }

            var tree = CSharpSyntaxTree.ParseText(output.Text, parseOptions, path: output.Path);
            var diagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (diagnostics.Length != 0)
            {
                throw new InvalidOperationException($"{output.Path} does not parse: {JoinDiagnostics(diagnostics)}");
            }
        }

        foreach (var inputType in inputTypes)
        {
            var declaration = GetTypeOwnedText(inputType);
            var count = outputs.Sum(o => CountOccurrences(o.Text, declaration));
            if (count != 1)
            {
                throw new InvalidOperationException($"{originalPath}: declaration and owned trivia for {inputType.Key} appears {count} times across outputs");
            }
        }

        VerifyLineConservation(originalPath, inputTypes, outputs);

        var outputTypes = outputs.SelectMany(o => TopLevelType.Find(CSharpSyntaxTree.ParseText(o.Text, parseOptions, path: o.Path).GetCompilationUnitRoot())).Select(t => t.Key).Order(StringComparer.Ordinal).ToArray();
        var inputKeys = inputTypes.Select(t => t.Key).Order(StringComparer.Ordinal).ToArray();
        if (!inputKeys.SequenceEqual(outputTypes, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{originalPath}: output top-level type set does not match input");
        }
    }

    /// <summary>Returns the text of a type's declaration together with its owned leading trivia (doc comments and attributes).</summary>
    private static string GetTypeOwnedText(TopLevelType type)
    {
        var fullText = type.Node.SyntaxTree.GetText().ToString();
        return fullText.Substring(GetTypeOwnedStart(type), type.Node.Span.End - GetTypeOwnedStart(type));
    }

    /// <summary>
    /// Returns the start position of a type's owned span. Ownership begins at the
    /// type's leading documentation comment when one exists; otherwise it begins at
    /// the type's declaration. This is what makes XML doc comments travel with the
    /// type they belong to.
    /// </summary>
    private static int GetTypeOwnedStart(TopLevelType type)
    {
        foreach (var trivia in type.Node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            {
                return trivia.SpanStart;
            }
        }

        return type.Node.SpanStart;
    }

    /// <summary>
    /// Counts non-whitespace source lines in the original file against the outputs
    /// and throws if a line was dropped, or if a non-prologue line was duplicated.
    /// This is the deepest correctness invariant of the tool: no line of user code is
    /// silently lost, and only prologue lines (lines outside every type's owned span,
    /// for example usings and the namespace, which legitimately appear in every
    /// output) are allowed to appear more than once across the outputs.
    /// </summary>
    /// <remarks>
    /// This method counts <see cref="OutputFile.BodyText"/>, which is the output text
    /// before any <c>--require-header</c> injection. That is deliberate: counting
    /// <see cref="OutputFile.Text"/> instead would let an injected header line mask a
    /// dropped source line.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a non-whitespace input line is not preserved across the outputs, or when an output line that was not in the prologue appears more times in the outputs than in the input.</exception>
    private static void VerifyLineConservation(string originalPath, IReadOnlyList<TopLevelType> inputTypes, IReadOnlyList<OutputFile> outputs)
    {
        var sourceText = inputTypes[0].Node.SyntaxTree.GetText().ToString();
        var prologueLines = GetPrologueLines(sourceText, inputTypes);
        var originalLines = CountNonWhitespaceLines(sourceText);
        var outputLines = CountOutputLines(outputs);

        foreach (var (line, count) in originalLines)
        {
            outputLines.TryGetValue(line, out var outputCount);
            if (outputCount < count)
            {
                throw new InvalidOperationException($"{originalPath}: non-whitespace line was dropped: {line}");
            }
        }

        foreach (var (line, count) in outputLines)
        {
            originalLines.TryGetValue(line, out var originalCount);
            if (count > originalCount && !prologueLines.Contains(line))
            {
                throw new InvalidOperationException($"{originalPath}: non-prologue line was duplicated: {line}");
            }
        }
    }

    /// <summary>
    /// Computes the set of "prologue" lines: non-whitespace source lines that fall
    /// outside every top-level type's owned span. These are the lines
    /// (usings, namespace declarations, and similar) that legitimately appear in
    /// every output file and are therefore exempt from the duplicate-line check in
    /// <see cref="VerifyLineConservation"/>.
    /// </summary>
    private static HashSet<string> GetPrologueLines(string sourceText, IReadOnlyList<TopLevelType> inputTypes)
    {
        var ownedRanges = inputTypes
            .Select(type => (Start: GetTypeOwnedStart(type), End: type.Node.Span.End))
            .OrderBy(range => range.Start)
            .ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var line in SplitLines(sourceText))
        {
            var lineEnd = position + line.Length;
            if (!string.IsNullOrWhiteSpace(line) && !ownedRanges.Any(range => position < range.End && lineEnd > range.Start))
            {
                result.Add(line);
            }

            position = lineEnd + GetLineBreakLength(sourceText, lineEnd);
        }

        return result;
    }

    private static Dictionary<string, int> CountOutputLines(IReadOnlyList<OutputFile> outputs)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            foreach (var (line, count) in CountNonWhitespaceLines(output.BodyText))
            {
                result[line] = result.TryGetValue(line, out var existing) ? existing + count : count;
            }
        }

        return result;
    }

    private static Dictionary<string, int> CountNonWhitespaceLines(string text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in SplitLines(text))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            result[line] = result.TryGetValue(line, out var count) ? count + 1 : 1;
        }

        return result;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
            {
                continue;
            }

            yield return text[start..i];
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static int GetLineBreakLength(string text, int lineEnd)
    {
        if (lineEnd >= text.Length)
        {
            return 0;
        }

        return text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1;
    }

    /// <summary>
    /// Runs the git-rename step in isolation and verifies that the file content is
    /// unchanged after the move. If content changed for any reason, the move is
    /// undone before reporting failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <c>git mv</c> altered the file's byte contents.</exception>
    private void ApplyRenameOnly(string originalPath, byte[] originalBytes, string keptPath)
    {
        RunGitMove(originalPath, keptPath);
        var movedBytes = File.ReadAllBytes(keptPath);
        if (!originalBytes.SequenceEqual(movedBytes))
        {
            RunGitMove(keptPath, originalPath);
            throw new InvalidOperationException($"git mv changed file content for {originalPath}");
        }
    }

    /// <summary>
    /// Writes every planned output file, using the encoding, BOM state, newline
    /// style, and final-newline convention captured from the original source.
    /// On any exception during writing, deletes the newly created sibling files and
    /// restores the original file from the bytes read at the start of processing so
    /// the working tree is left in its pre-run state.
    /// </summary>
    /// <remarks>
    /// The local <c>moved</c> flag inside the catch block is always false, so the
    /// <c>git mv</c> rollback branch is currently unreachable. Kept as a placeholder
    /// for a future path where rename and content happen in one call. Do not "fix"
    /// this by removing the branch; do it by wiring the flag correctly.
    /// </remarks>
    private void WriteOutputs(string originalPath, byte[] originalBytes, EncodedSource source, SplitPlan plan, IReadOnlyList<OutputFile> outputs)
    {
        var created = new List<string>();
        var moved = false;
        try
        {
            foreach (var output in outputs)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(output.Path, originalPath) && !StringComparer.OrdinalIgnoreCase.Equals(output.Path, plan.KeptPath))
                {
                    created.Add(output.Path);
                }

                WriteEncoded(output.Path, output.Text, source);
            }
        }
        catch
        {
            foreach (var path in created.Where(File.Exists))
            {
                File.Delete(path);
            }

            if (moved && File.Exists(plan.KeptPath))
            {
                RunGitMove(plan.KeptPath, originalPath);
            }

            File.WriteAllBytes(originalPath, originalBytes);
            throw;
        }
    }

    /// <summary>Runs <c>git mv</c> from the configured repository root and throws with stderr and stdout attached on failure.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the git process cannot be started or exits with a non-zero code.</exception>
    private void RunGitMove(string source, string target)
    {
        var psi = new ProcessStartInfo("git", $"mv \"{source}\" \"{target}\"")
        {
            WorkingDirectory = _options.RepoRoot,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("failed to start git mv");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("git mv failed: " + process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd());
        }
    }

    /// <summary>
    /// Writes the text to disk, preserving the source encoding and BOM. When the
    /// source had no BOM, the output has no BOM either; this is the point where the
    /// tool avoids gratuitous whole-file diffs on re-serialization.
    /// </summary>
    private static void WriteEncoded(string path, string text, EncodedSource source)
    {
        var body = source.Encoding.GetBytes(text);
        if (!source.EmitPreamble)
        {
            File.WriteAllBytes(path, body);
            return;
        }

        var preamble = source.Encoding.GetPreamble();
        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// Streams input paths from a directory (every <c>.cs</c> file beneath it), from a
    /// single <c>.cs</c> file, from standard input when the path is <c>-</c>, from a CSV
    /// file (which must contain a <c>file</c> column), or from a newline-delimited text
    /// file. Empty lines and empty <c>file</c> values are skipped.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the CSV input has no <c>file</c> column.</exception>
    private static IEnumerable<string> ReadInputs(string inputPath)
    {
        if (inputPath == Options.StdinPath)
        {
            return ReadLinePaths(ReadStandardInputLines());
        }

        if (Directory.Exists(inputPath))
        {
            return DiscoverSourceFiles(inputPath);
        }

        // A .cs file is the file to split, not a list of paths. Reading it as a list would
        // feed every line of C# in as a path and report a screen of "input file does not
        // exist" skips at exit code 0, which reads like success.
        if (Path.GetExtension(inputPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return [inputPath];
        }

        if (Path.GetExtension(inputPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return ReadCsvPaths(inputPath);
        }

        return ReadLinePaths(File.ReadLines(inputPath));
    }

    private static IEnumerable<string> ReadStandardInputLines()
    {
        while (Console.In.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// Enumerates every <c>.cs</c> file beneath a directory, skipping <c>bin</c> and
    /// <c>obj</c> segments. Those hold compiler and generator output that a build
    /// recreates, so splitting them would churn files nobody reads. That is a fact
    /// about how .NET lays out a build rather than an opinion about one repository,
    /// which is the line decision 14 draws around <c>--exclude</c>.
    /// </summary>
    /// <remarks>
    /// Walks the tree by hand rather than using <see cref="EnumerationOptions"/>. That
    /// type's <c>AttributesToSkip</c> applies to files as well as directories, so skipping
    /// reparse points there would silently drop a symlinked source file, which is a second
    /// undocumented exclusion. Here only directory symlinks are cut, which is what stops a
    /// link pointing at an ancestor from recursing forever.
    /// </remarks>
    private static IEnumerable<string> DiscoverSourceFiles(string root)
    {
        // The whole list is materialized before any file is processed, so an unreadable
        // directory fails the run before the content phase has rewritten anything.
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly));

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    var isBuildOutput = name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase);
                    var isLink = new DirectoryInfo(child).LinkTarget is not null;

                    if (!isBuildOutput && !isLink)
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Not swallowed. A partial scan exits 0 and looks exactly like a scan that
                // found no work, which is the one outcome an agent cannot recover from.
                throw new InvalidOperationException(
                    $"cannot scan directory {directory}: {ex.Message}. Pass a list file instead of a directory to skip the unreadable part.",
                    ex);
            }
        }

        // A file symlink and its target can both be inside the scanned tree. They are two
        // paths naming one file, and processing both would apply the split twice: the second
        // pass sees an already-split file and reports nothing to do, so one planned output is
        // silently attributed to the wrong path. De-duplicate by symlink target, and keep the
        // real path rather than the link so the manifest names the file git tracks. Aliases
        // that are not reparse points, hard links and bind mounts, are not detected.
        return files
            .Select(f => (Path: f, Identity: PhysicalIdentity(f)))
            .OrderBy(t => !PathComparison.Comparer.Equals(t.Path, t.Identity))
            .ThenBy(t => t.Path, StringComparer.Ordinal)
            .DistinctBy(t => t.Identity, PathComparison.Comparer)
            .Select(t => t.Path)
            .ToArray();
    }

    /// <summary>
    /// Resolves a path to the file it ultimately names, so a symlink and its target compare
    /// equal. Falls back to the path itself when the link cannot be resolved, which errs
    /// toward processing the file rather than dropping it.
    /// </summary>
    private static string PhysicalIdentity(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static IEnumerable<string> ReadCsvPaths(string inputPath)
    {
        using var parser = new CsvFieldReader(inputPath);
        var header = parser.ReadFields() ?? Array.Empty<string>();
        var fileIndex = Array.FindIndex(header, h => h.Equals("file", StringComparison.OrdinalIgnoreCase));
        if (fileIndex < 0)
        {
            throw new InvalidOperationException("CSV must contain a file column.");
        }

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is not null && fields.Length > fileIndex && !string.IsNullOrWhiteSpace(fields[fileIndex]))
            {
                yield return fields[fileIndex];
            }
        }
    }

    private static IEnumerable<string> ReadLinePaths(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                yield return trimmed;
            }
        }
    }

    /// <summary>
    /// De-duplicates the input list. Every surviving path produces a manifest row, including
    /// excluded ones. Comparison uses <see cref="PathComparison.Comparer"/> so that
    /// de-duplication and the content phase's manifest lookup agree on what counts as the
    /// same file.
    /// </summary>
    /// <remarks>
    /// Paths are resolved to absolute form before de-duplication, not after. A caller can
    /// list the same file as <c>src/Foo.cs</c> and as <c>C:\repo\src\Foo.cs</c>, and the two
    /// strings differ, so de-duplicating first let one file be split twice. The consumers
    /// resolve to the same absolute path a moment later, which is what makes the duplicate
    /// invisible until it has already rewritten a file.
    /// </remarks>
    private static IEnumerable<string> ReadRunnableInputs(string inputPath)
    {
        return ReadInputs(inputPath).Select(Path.GetFullPath).Distinct(PathComparison.Comparer);
    }

    /// <summary>
    /// Returns the first <c>--exclude</c> pattern matching <paramref name="path"/>, or null
    /// when none match. Both the path and the pattern are normalized to forward slashes and
    /// compared case-insensitively so one pattern works on Windows and Linux alike.
    /// </summary>
    private string? MatchExclude(string path)
    {
        var patterns = _options.Excludes;
        if (patterns is null || patterns.Count == 0)
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        return patterns.FirstOrDefault(pattern =>
            pattern.Length != 0 && normalized.Contains(pattern.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="value"/> in <paramref name="text"/>, using ordinal comparison.</summary>
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>Renders a list of Roslyn diagnostics as a single semicolon-separated string, used to build refusal reasons.</summary>
    private static string JoinDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        return string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.GetMessage()}"));
    }
}
