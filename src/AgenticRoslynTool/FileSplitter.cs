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
/// output file per ordinary top-level type. A <c>file</c> type stays in the kept file. All parsing and rewriting go through the Roslyn
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
/// written and is recorded as <c>failed</c>: in the manifest when the plan phase finds it,
/// and in the run report when the content phase does.
/// </para>
/// </remarks>
internal sealed class FileSplitter
{
    private readonly Options _options;

    private readonly WorkspaceWriter _writer;

    /// <summary>
    /// Reason recorded when the content phase is handed a file the plan never covered.
    /// Published: the packaged skill counts this skip, and <see cref="AlreadyAppliedReason"/>,
    /// as the only skips that leave no planned work behind. <c>PackagedAgentDocsTests</c>
    /// fails if the skill stops quoting it.
    /// </summary>
    internal const string NotPlannedReason = "not present as split in plan manifest";

    /// <summary>
    /// Reason recorded when a content run meets a row an earlier content run already applied.
    /// The words say what was checked and nothing more. The leading <c>already applied</c> is
    /// published: callers match on it to tell this skip from a refusal, so it must not change.
    /// </summary>
    internal const string AlreadyAppliedReason = "already applied: every planned output exists and this file no longer declares the planned types";

    /// <summary>Constructs a splitter bound to a set of parsed options.</summary>
    /// <param name="options">The options that select the input list, manifest path, repository root, phase, and optional required header.</param>
    public FileSplitter(Options options)
    {
        _options = options;
        _writer = new WorkspaceWriter(options.RepoRoot);
    }

    /// <summary>
    /// Runs the phase selected by <see cref="Options.Phase"/>.
    /// </summary>
    /// <returns>
    /// One row per input describing what the run did. The plan phase's rows are the manifest.
    /// The rows of the other phases are the run report, and never reach the manifest.
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown if the phase value is unrecognized, if the content phase finds no plan manifest, if the renames or content phase finds a manifest with no <c>contentHash</c> or <c>collisionNaming</c> column, or if <c>--collision-naming</c> contradicts it.</exception>
    public IReadOnlyList<FileResult> Run() => Run(out _);

    /// <inheritdoc cref="Run()"/>
    /// <param name="collisionNaming">
    /// The collision naming this run used, after <see cref="CollisionNamingFlag.Resolve"/>.
    /// <c>Program</c> records it in the manifest or the report, so it is the resolved value
    /// and never the raw flag.
    /// </param>
    public IReadOnlyList<FileResult> Run(out CollisionNaming collisionNaming)
    {
        (var rows, collisionNaming) = _options.Phase switch
        {
            Phase.Plan => RunPlan(),
            Phase.Renames => RunRenames(),
            Phase.Content => RunContent(),
            _ => throw new InvalidOperationException($"Unsupported phase: {_options.Phase}"),
        };
        return rows;
    }

    /// <summary>Plan phase entry point. Builds the resolved plan and returns it without writing anything.</summary>
    /// <remarks>
    /// A plan starts afresh, so it never reads an existing manifest's naming. An absent flag
    /// means <see cref="CollisionNaming.FileFirst"/>, and the value is then recorded for the
    /// phases that follow.
    /// </remarks>
    private (IReadOnlyList<FileResult> Rows, CollisionNaming Naming) RunPlan()
    {
        var collisionNaming = _options.CollisionNaming ?? CollisionNaming.FileFirst;
        return (KeptRenameCollisions.Resolve(BuildResolvedPlan(collisionNaming)), collisionNaming);
    }

    /// <summary>
    /// Renames phase entry point. Recomputes the plan and performs the <c>git mv</c>
    /// for every split row whose kept file name changes. Content edits are not
    /// performed in this phase, so the resulting commit contains only renames.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This phase recomputes the plan, because a rename has to follow the file as it is now.
    /// It never writes the manifest: the rows it returns go to the run report. When a
    /// manifest exists, the phase is bound to it. It renames only a file the manifest marks
    /// <c>split</c> whose bytes still match the recorded hash, and no row it returns carries
    /// a hash of its own. See <see cref="PlanBinding.HoldRenamesRow"/>. It also runs a rename
    /// only when the manifest row plans that same rename. See
    /// <see cref="KeptRenameCollisions.HoldToPlannedRename"/>.
    /// </para>
    /// <para>
    /// With no manifest on disk nothing was planned, so there is nothing to be held to, and
    /// the phase plans and hashes afresh as the plan phase would. It still writes no
    /// manifest, so the content phase cannot run until the plan phase has.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a manifest exists and has no <c>contentHash</c> or <c>collisionNaming</c> column, or when <c>--collision-naming</c> contradicts it.</exception>
    private (IReadOnlyList<FileResult> Rows, CollisionNaming Naming) RunRenames()
    {
        var priorPlan = File.Exists(_options.ManifestPath)
            ? SplitPlanner.IndexByInputPath(ManifestWriter.Read(_options.ManifestPath, requireContentHash: true))
            : null;

        // This phase reruns the collision pass, so it names collisions the way the plan did.
        // A different value would compute paths the reviewed plan never held.
        var recorded = priorPlan is null ? null : ManifestCollisionNaming.Read(_options.ManifestPath);
        var collisionNaming = CollisionNamingFlag.Resolve(_options.CollisionNaming, recorded, _options.ManifestPath);

        // Collisions are resolved first, over every row, and the rows are bound to the plan
        // after. A file refused here still took part in naming, so the inputs that are
        // applied keep the qualified paths the reviewed plan gave them. Every row is bound
        // before the first rename, so no binding sees a tree this run already changed.
        // Rename targets are handed out last, so a row this phase refuses never takes a
        // target from a row that will act. A rename that is not the plan's own is dropped
        // first, because the content phase reads the kept path the manifest holds.
        var bound = BuildResolvedPlan(collisionNaming).Select(row => priorPlan is null ? row : KeptRenameCollisions.HoldToPlannedRename(PlanBinding.HoldRenamesRow(row, priorPlan), priorPlan)).ToArray();
        return (KeptRenameCollisions.Resolve(bound).Select(ApplyPlannedRename).ToArray(), collisionNaming);
    }

    /// <summary>
    /// Performs the rename one planned row asks for. A row that asks for none is returned
    /// as planned. A rename that fails becomes a <c>failed</c> row, for the reason
    /// <see cref="Process"/> gives: an exception that escapes here would abort the run after
    /// earlier renames landed, with no report naming them.
    /// </summary>
    private FileResult ApplyPlannedRename(FileResult planned)
    {
        if (planned.Status != "split" || !planned.GitMove)
        {
            return planned;
        }

        // git mv moves a file into a directory that sits at its destination. The plan only
        // asked whether a file was there, so this is checked before git is asked. A look
        // that fails reads as no directory, and git then answers for that path itself.
        if (Directory.Exists(planned.KeptPath))
        {
            return FileResult.Failed(planned.OriginalPath, planned.OriginalPath, $"rename to {planned.KeptPath} failed: a directory is at that path") with { GitMove = true, ContentHash = planned.ContentHash };
        }

        try
        {
            // The bytes moved are read here, after the plan pass that hashed the file. An
            // edit landing in between must not be renamed under a hash it does not match.
            var bytes = File.ReadAllBytes(planned.OriginalPath);
            var changed = ContentHash.DescribeMismatch(planned.ContentHash, ContentHash.Compute(bytes));
            if (changed is not null)
            {
                return FileResult.StalePlan(planned.OriginalPath, planned.OriginalPath, changed, planned.ContentHash);
            }

            _writer.ApplyRenameOnly(planned.OriginalPath, bytes, planned.KeptPath);
            return planned;
        }
        catch (Exception ex)
        {
            // The row says where the file is. That is the original path unless git mv
            // landed and a later step threw, and either way the plan still asks for the move.
            var location = File.Exists(planned.OriginalPath) ? planned.OriginalPath : planned.KeptPath;
            return FileResult.Failed(planned.OriginalPath, location, $"rename to {planned.KeptPath} failed: {ex.Message}") with { GitMove = true, ContentHash = planned.ContentHash };
        }
    }

    /// <summary>Runs the plan for every input path and resolves any cross-file output-name collisions into their qualified form.</summary>
    private IReadOnlyList<FileResult> BuildResolvedPlan(CollisionNaming collisionNaming)
    {
        var results = new List<FileResult>();
        foreach (var path in InputSource.ReadRunnableInputs(_options.InputPath).ToArray())
        {
            results.Add(Process(path, Phase.Plan, null, collisionNaming));
        }

        return SplitPlanner.ResolvePlannedOutputCollisions(results, collisionNaming);
    }

    /// <summary>
    /// Content phase entry point. Reads the plan manifest, then for each input path
    /// runs <see cref="Process"/> against the planned row. An input path matches a row by
    /// its original path, or by its kept path once the renames phase has moved it (see
    /// <see cref="SplitPlanner.IndexByInputPath"/>). Files not present as <c>split</c> in
    /// the manifest are skipped with a diagnostic reason.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the manifest file does not exist, has no <c>contentHash</c> or <c>collisionNaming</c> column, or when <c>--collision-naming</c> contradicts it.</exception>
    /// <remarks>
    /// The input list is materialized before the loop. Enumeration itself can fail, on an
    /// unreadable list file or a malformed path, and the content phase rewrites source as it
    /// goes. Streaming would let an early input be rewritten and a later one abort the run
    /// before the report is written, leaving changed files and no record of which. Per-file
    /// failures inside the loop are handled the same way for the same reason: <see cref="Process"/>
    /// turns any failure on one input, expected or not, into a row rather than an abort, so the
    /// report describes what the run actually did. When the report file itself cannot be
    /// written, <c>Program</c> prints the rows to standard output instead, so the record
    /// survives that too.
    /// </remarks>
    private (IReadOnlyList<FileResult> Rows, CollisionNaming Naming) RunContent()
    {
        if (!File.Exists(_options.ManifestPath))
        {
            throw new InvalidOperationException($"Content phase requires an existing plan manifest: {_options.ManifestPath}");
        }

        var inputs = InputSource.ReadRunnableInputs(_options.InputPath).ToArray();

        // The manifest is read and never written back. Only the split rows describe work,
        // so only they are indexed.
        //
        // Read already collapses the CSV to one row per path, so nothing re-groups here.
        var manifestPlan = ManifestWriter.Read(_options.ManifestPath, requireContentHash: true);
        var collisionNaming = CollisionNamingFlag.Resolve(_options.CollisionNaming, ManifestCollisionNaming.Read(_options.ManifestPath), _options.ManifestPath);
        var plannedFiles = SplitPlanner.IndexByInputPath(manifestPlan.Where(r => r.Status == "split"));

        // Every row, under the same paths the split index uses, so an input handed over by
        // its kept path still finds the row the plan recorded for it.
        var recordedRows = SplitPlanner.IndexByInputPath(manifestPlan);

        var results = new List<FileResult>();
        var applied = new HashSet<string>(PathComparison.Comparer);
        foreach (var path in inputs)
        {
            var inputPath = Path.GetFullPath(path);
            if (!plannedFiles.TryGetValue(inputPath, out var planned))
            {
                // The plan may still hold a row for this file, skipped or failed. The report
                // row repeats that row's hash so the two documents agree on it.
                recordedRows.TryGetValue(inputPath, out var recorded);
                results.Add(FileResult.Skip(inputPath, inputPath, NotPlannedReason) with { ContentHash = recorded?.ContentHash });
                continue;
            }

            // A directory scan after the renames phase hands over the kept path, and a list
            // can name both paths of one file. Either way the plan row is the identity, so a
            // row already applied in this run is not applied twice.
            if (applied.Add(planned.OriginalPath))
            {
                results.Add(Process(planned.OriginalPath, Phase.Content, planned, collisionNaming));
            }
        }

        // A planned split the current input never mentioned is reported as a skip, so a
        // caller applying content in batches sees what is still owed. The plan itself is
        // untouched on disk, so the next batch reads the same rows.
        var unapplied = manifestPlan
            .Where(p => p.Status == "split" && !applied.Contains(p.OriginalPath))
            .Select(p => FileResult.Skip(
                p.OriginalPath,
                p.KeptPath,
                "planned as split but not supplied to the content phase input") with { ContentHash = p.ContentHash });

        return (results.Concat(unapplied).ToArray(), collisionNaming);
    }

    /// <summary>
    /// Processes one input file through the requested phase: parses it, checks the
    /// preconditions that would make splitting unsafe, builds a plan, and (in the
    /// content phase) verifies and writes outputs. All refusals become <c>skipped</c>
    /// results with a diagnostic reason; unexpected exceptions become <c>failed</c>.
    /// </summary>
    /// <param name="path">Absolute or relative input path.</param>
    /// <param name="phase"><see cref="Phase.Plan"/> or <see cref="Phase.Content"/>. The renames phase plans with <see cref="Phase.Plan"/>.</param>
    /// <param name="planned">The manifest row for this input, or null in the plan and renames phases.</param>
    /// <param name="collisionNaming">The run's resolved collision naming. Read only by the content phase's manifest path check.</param>
    /// <returns>A result describing the outcome for this input.</returns>
    /// <remarks>
    /// The catch is unfiltered on purpose, and for the same reason the one in
    /// <c>Program.Main</c> is. The content phase rewrites files as it goes and writes the
    /// report at the end, so anything that escapes this method strands every file already
    /// rewritten with no record of them. A filtered catch only holds until some path throws a
    /// type nobody listed, and this method reaches an inconsistent rename state, a hand-edited
    /// manifest, and the file system. One bad input costs a failed row; one escaped exception
    /// costs the record of the whole run.
    /// </remarks>
    private FileResult Process(string path, Phase phase, FileResult? planned, CollisionNaming collisionNaming)
    {
        try
        {
            return PlanBinding.CarryPlannedFacts(ProcessCore(path, phase, planned, collisionNaming), planned);
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

            return PlanBinding.CarryPlannedFacts(FileResult.Failed(reported, reported, ex.Message), planned);
        }
    }

    /// <summary>
    /// Implements <see cref="Process"/>. Kept separate so every exit path, including an
    /// unexpected throw, funnels through the one guard in the caller.
    /// </summary>
    /// <param name="path">Absolute or relative input path.</param>
    /// <param name="phase"><see cref="Phase.Plan"/> or <see cref="Phase.Content"/>. The renames phase plans with <see cref="Phase.Plan"/>.</param>
    /// <param name="planned">The manifest row for this input, or null in the plan and renames phases.</param>
    /// <param name="collisionNaming">The run's resolved collision naming. Read only by the content phase's manifest path check.</param>
    /// <returns>A result describing the outcome for this input.</returns>
    private FileResult ProcessCore(string path, Phase phase, FileResult? planned, CollisionNaming collisionNaming)
    {
        var originalPath = Path.GetFullPath(path);
        var excluded = MatchExclude(originalPath);
        if (excluded is not null)
        {
            return FileResult.Skip(originalPath, originalPath, $"excluded by pattern: {excluded}");
        }

        var readPath = SplitPlanner.GetReadPath(originalPath, phase, planned);
        try
        {
            return ProcessResolved(originalPath, readPath, phase, planned, collisionNaming);
        }
        catch (Exception ex)
        {
            // Everything below reads or plans against readPath, so readPath is where the
            // file is. The guard in the caller only knows the pre-rename path, which a
            // landed rename has emptied, and a row naming an empty path sends the next run
            // looking in the wrong place.
            return FileResult.Failed(originalPath, readPath, ex.Message);
        }
    }

    /// <summary>
    /// Implements <see cref="ProcessCore"/> from the point the file's real location is
    /// known. Split out so one catch covers every throw below it, not just the ones that
    /// carry their own filter.
    /// </summary>
    private FileResult ProcessResolved(string originalPath, string readPath, Phase phase, FileResult? planned, CollisionNaming collisionNaming)
    {
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
            // rewritten in this run with no report recording them.
            return FileResult.Failed(originalPath, readPath, $"cannot read input: {ex.Message}");
        }

        // Hashed from the bytes already in hand, so the file is read once and the hash
        // describes exactly what the rest of this method parses, verifies, and rewrites.
        var contentHash = ContentHash.Compute(originalBytes);
        if (phase == Phase.Content && planned is not null)
        {
            // First check after the read, ahead of every structural one. The plan was
            // reviewed against specific bytes, and an edit that keeps every type name and
            // path would pass each check below and be applied unreviewed. A skip and not a
            // throw: a stale plan is a refusal, the same as the manifest mismatch.
            //
            // The row keeps the planned hash, so no row this phase reports ever carries a
            // hash the plan phase did not record.
            //
            // One changed file is not an outside edit: the file an earlier content run of
            // this same plan already split. A retry meets it, and it gets its own reason.
            // Either way the file is skipped and nothing is written.
            var stale = ContentHash.DescribeMismatch(planned.ContentHash, contentHash);
            if (stale is not null)
            {
                return IsAlreadyApplied(originalBytes, readPath, planned)
                    ? FileResult.Skip(originalPath, readPath, AlreadyAppliedReason) with { ContentHash = planned.ContentHash }
                    : FileResult.StalePlan(originalPath, readPath, stale, planned.ContentHash);
            }
        }

        try
        {
            return ProcessBytes(originalPath, readPath, originalBytes, phase, planned, collisionNaming) with { ContentHash = contentHash };
        }
        catch (Exception ex)
        {
            // Same row the catch in ProcessCore would build, plus the hash. The bytes were
            // read, so the row says which bytes the failure was about.
            return FileResult.Failed(originalPath, readPath, ex.Message) with { ContentHash = contentHash };
        }
    }

    /// <summary>
    /// True when bytes that no longer match the plan are what an earlier content run of the
    /// same plan left behind. Only asked about a file whose hash differs from the plan: a
    /// file that still matches has not been split. Anything that does not parse cleanly is
    /// not applied work, and stays a stale plan.
    /// </summary>
    private static bool IsAlreadyApplied(byte[] bytes, string readPath, FileResult planned)
    {
        EncodedSource source;
        try
        {
            source = EncodedSource.FromBytes(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source.Text, source.Encoding), SourceParsing.Options, path: readPath);
        return SourceParsing.Errors(tree).Length == 0
            && SplitPlanner.IsAlreadyApplied(planned, TopLevelType.Find(tree.GetCompilationUnitRoot()).ToArray());
    }

    /// <summary>
    /// Implements <see cref="ProcessResolved"/> from the point the input's bytes are in
    /// hand. Split out so the caller can stamp one content hash on every row this returns,
    /// whichever of its exits produced it.
    /// </summary>
    private FileResult ProcessBytes(string originalPath, string readPath, byte[] originalBytes, Phase phase, FileResult? planned, CollisionNaming collisionNaming)
    {
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
        var parseOptions = SourceParsing.Options;
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source.Text, source.Encoding), parseOptions, path: readPath);
        var root = tree.GetCompilationUnitRoot();
        var originalDiagnostics = SourceParsing.Errors(tree);
        if (originalDiagnostics.Length != 0)
        {
            return FileResult.Failed(originalPath, readPath, "input has syntax errors: " + OutputVerifier.JoinDiagnostics(originalDiagnostics));
        }

        // Checked before top-level statements, because a file-based program can declare
        // Main in a class and have none. Its types still cannot leave the file.
        if (SourceParsing.IsFileBasedProgram(root))
        {
            return FileResult.Skip(originalPath, readPath, SourceParsing.FileBasedProgramReason);
        }

        if (root.Members.OfType<GlobalStatementSyntax>().Any())
        {
            return FileResult.Skip(originalPath, readPath, "contains top-level statements; manual split required");
        }

        var types = TopLevelType.Find(root).ToArray();
        if (types.Length <= 1)
        {
            return FileResult.Skip(originalPath, readPath, $"nothing to split: input has {types.Length} top-level type declaration(s)");
        }

        // A file-local type stays where it is. This refuses the inputs no choice of kept
        // type could make safe. BuildPlan refuses the rest, once it knows which types move.
        if (FileLocalTypes.DescribeRefusal(types) is { } fileLocalRefusal)
        {
            return FileResult.Skip(originalPath, readPath, fileLocalRefusal.Reason) with { Note = fileLocalRefusal.Note };
        }

        var directiveSafety = DirectiveAnalyzer.AnalyzeDirectiveSafety(root, types);
        if (!directiveSafety.IsSafe)
        {
            return FileResult.Skip(originalPath, readPath, directiveSafety.Reason ?? "contains unsafe directive; manual split required");
        }

        var plan = SplitPlanner.BuildPlan(originalPath, readPath, types, planned, directiveNote: directiveSafety.Note, collisionNaming: collisionNaming, convention: _options.FileNaming);
        // A refused row reports readPath, where the file is. plan.KeptPath can be a rename
        // target that nothing moved the file to, because a refusal never renames.
        if (plan.SkipReason is not null)
        {
            var skipped = FileResult.Skip(originalPath, readPath, plan.SkipReason);
            return plan.SkipNote is null ? skipped : skipped with { Note = plan.SkipNote };
        }

        if (DirectiveAnalyzer.ProducesEmptyDirectiveShell(root, types, plan))
        {
            return FileResult.Skip(originalPath, readPath, "splitting would leave an empty directive shell; manual split required");
        }

        // No caller passes Phase.Renames. The renames phase plans through here with
        // Phase.Plan and runs its git mv later, in ApplyPlannedRename.
        try
        {
            var headerText = OutputBuilder.NormalizeHeaderText(_options.RequiredHeader, source.NewLine);
            var outputs = OutputBuilder.BuildOutputs(source, root, types, plan, headerText);
            OutputVerifier.VerifyOutputs(readPath, types, outputs, parseOptions, headerText);

            if (phase == Phase.Content)
            {
                _writer.WriteOutputs(readPath, originalBytes, source, plan, outputs);
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
            // Nothing here moves the file. Everything before WriteOutputs leaves it alone,
            // and WriteOutputs writes the input back to readPath on failure.
            return FileResult.Failed(originalPath, readPath, ex.Message);
        }
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
}
