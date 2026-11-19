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

    private readonly WorkspaceWriter _writer;

    /// <summary>
    /// Reason recorded when the content phase is handed a file the plan never covered. Named
    /// because the reason string decides whether the row reaches the manifest.
    /// </summary>
    private const string NotPlannedReason = "not present as split in plan manifest";

    /// <summary>Reason recorded when the renames phase would split a file the manifest on disk has no row for.</summary>
    private const string NoPlanRowReason = ContentHash.StalePlanPrefix + "the plan manifest has no row for this file; re-run --phase plan";

    /// <summary>Reason recorded when the renames phase would split a file the manifest on disk records as skipped or failed.</summary>
    private const string NotPlannedAsSplitReason = ContentHash.StalePlanPrefix + "the plan manifest does not mark this file as split; re-run --phase plan";

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
    /// <returns>The manifest and report views of the run.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the phase value is unrecognized, if the content phase finds no plan manifest, or if the renames or content phase finds a manifest with no <c>contentHash</c> column.</exception>
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
    /// <remarks>
    /// <para>
    /// This phase recomputes the plan, and <c>Program</c> writes the result over the manifest.
    /// Left unchecked, that would launder an edit made after the plan: the new row would
    /// carry the hash of the edited bytes, and the content phase would find a match. So when
    /// a manifest exists, this phase is bound to it. It renames only a file the manifest
    /// marks <c>split</c> whose bytes still match the recorded hash, and it never records a
    /// hash of its own. See <see cref="HoldToPlan"/>.
    /// </para>
    /// <para>
    /// With no manifest on disk nothing was planned, so there is nothing to be held to, and
    /// the phase plans and hashes afresh as the plan phase would.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a manifest exists and has no <c>contentHash</c> column.</exception>
    private IReadOnlyList<FileResult> RunRenames()
    {
        var priorPlan = File.Exists(_options.ManifestPath)
            ? SplitPlanner.IndexByInputPath(ManifestWriter.Read(_options.ManifestPath, requireContentHash: true))
            : null;

        // Collisions are resolved first, over every row, and the rows are bound to the plan
        // after. A file refused here still took part in naming, so the inputs that are
        // applied keep the qualified paths the reviewed plan gave them.
        var results = BuildResolvedPlan().Select(row => priorPlan is null ? row : HoldToPlan(row, priorPlan)).ToArray();
        for (var i = 0; i < results.Length; i++)
        {
            var result = results[i];
            if (result.Status != "split" || !result.GitMove)
            {
                continue;
            }

            // The bytes moved are read here, after the plan pass that hashed the file. An
            // edit landing in between must not be renamed under a hash it does not match.
            var bytes = File.ReadAllBytes(result.OriginalPath);
            var changed = ContentHash.DescribeMismatch(result.ContentHash, ContentHash.Compute(bytes));
            if (changed is not null)
            {
                results[i] = FileResult.StalePlan(result.OriginalPath, result.OriginalPath, changed, result.ContentHash);
                continue;
            }

            _writer.ApplyRenameOnly(result.OriginalPath, bytes, result.KeptPath);
        }

        return results;
    }

    /// <summary>Runs the plan for every input path and resolves any cross-file output-name collisions into their qualified form.</summary>
    private IReadOnlyList<FileResult> BuildResolvedPlan()
    {
        var results = new List<FileResult>();
        foreach (var path in InputSource.ReadRunnableInputs(_options.InputPath).ToArray())
        {
            results.Add(Process(path, Phase.Plan, null));
        }

        return SplitPlanner.ResolvePlannedOutputCollisions(results);
    }

    /// <summary>
    /// Binds one freshly computed renames row to the manifest already on disk.
    /// </summary>
    /// <remarks>
    /// Three rules, and each closes a path the others leave open.
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
    /// A refusal keeps the recorded hash, so it stays a refusal on the next run. Only the
    /// plan phase starts a new plan.
    /// </remarks>
    private static FileResult HoldToPlan(FileResult row, Dictionary<string, FileResult> priorPlan)
    {
        var wouldAct = row.Status == "split";

        // The rename has not happened, so a refused file is still at the path it was read from.
        if (!priorPlan.TryGetValue(row.OriginalPath, out var prior))
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
    /// Content phase entry point. Reads the plan manifest, then for each input path
    /// runs <see cref="Process"/> against the planned row. An input path matches a row by
    /// its original path, or by its kept path once the renames phase has moved it (see
    /// <see cref="SplitPlanner.IndexByInputPath"/>). Files not present as <c>split</c> in
    /// the manifest are skipped with a diagnostic reason.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the manifest file does not exist, or has no <c>contentHash</c> column.</exception>
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

        var inputs = InputSource.ReadRunnableInputs(_options.InputPath).ToArray();

        // Read once and keep every row. The split-only view below decides what this run can
        // act on; the full set is what gets preserved, because a skipped or failed row is
        // part of the plan of record and a content run must not quietly drop it.
        //
        // Read already collapses the CSV to one row per path, so nothing re-groups here.
        var manifestPlan = ManifestWriter.Read(_options.ManifestPath, requireContentHash: true);
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
                results.Add(Process(planned.OriginalPath, Phase.Content, planned));
            }
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
                "planned as split but not supplied to the content phase input") with { ContentHash = p.ContentHash })).ToArray();

        return new RunOutcome(manifestRows, reportRows);
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
            return WithPlannedFacts(ProcessCore(path, phase, planned), planned);
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

            return WithPlannedFacts(FileResult.Failed(reported, reported, ex.Message), planned);
        }
    }

    /// <summary>
    /// Restores the two facts about the plan that the refusal and failure factories drop: the
    /// pending-rename flag and the content hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whether a rename is owed is a fact about the plan, not about how this run ended, so a
    /// row that forgets it tells the next reader no move is pending for a file that still
    /// needs one. The paths are deliberately left alone: they say where the file is, which a
    /// planned row cannot answer for a run that refused before the rename landed.
    /// </para>
    /// <para>
    /// The hash is the same kind of fact. Every row the content phase writes for a planned
    /// file replaces that file's manifest row, including the exits that never read the file:
    /// an excluded path, a missing file, an inconsistent rename state. A row that dropped the
    /// hash there would leave the next phase nothing to hold the file to. Carried on every
    /// status, so the content phase can never change a recorded hash.
    /// </para>
    /// </remarks>
    private static FileResult WithPlannedFacts(FileResult result, FileResult? planned)
    {
        if (planned is null)
        {
            return result;
        }

        var carried = result with { ContentHash = planned.ContentHash };
        return result.Status == "split" ? carried : carried with { GitMove = planned.GitMove };
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

        var readPath = SplitPlanner.GetReadPath(originalPath, phase, planned);
        try
        {
            return ProcessResolved(originalPath, readPath, phase, planned);
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
    private FileResult ProcessResolved(string originalPath, string readPath, Phase phase, FileResult? planned)
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
            // rewritten in this run with no manifest recording them.
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
            // The row keeps the planned hash. It is written back to the manifest, and a row
            // carrying the hash of the edited bytes would let a later run find a match.
            var stale = ContentHash.DescribeMismatch(planned.ContentHash, contentHash);
            if (stale is not null)
            {
                return FileResult.StalePlan(originalPath, readPath, stale, planned.ContentHash);
            }
        }

        try
        {
            return ProcessBytes(originalPath, readPath, originalBytes, phase, planned) with { ContentHash = contentHash };
        }
        catch (Exception ex)
        {
            // Same row the catch in ProcessCore would build, plus the hash. The bytes were
            // read, so the row says which bytes the failure was about.
            return FileResult.Failed(originalPath, readPath, ex.Message) with { ContentHash = contentHash };
        }
    }

    /// <summary>
    /// Implements <see cref="ProcessResolved"/> from the point the input's bytes are in
    /// hand. Split out so the caller can stamp one content hash on every row this returns,
    /// whichever of its exits produced it.
    /// </summary>
    private FileResult ProcessBytes(string originalPath, string readPath, byte[] originalBytes, Phase phase, FileResult? planned)
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
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source.Text, source.Encoding), parseOptions, path: readPath);
        var root = tree.GetCompilationUnitRoot();
        var originalDiagnostics = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (originalDiagnostics.Length != 0)
        {
            return FileResult.Failed(originalPath, readPath, "input has syntax errors: " + OutputVerifier.JoinDiagnostics(originalDiagnostics));
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

        if (types.Any(t => t.HasFileModifier))
        {
            return FileResult.Skip(originalPath, readPath, "contains file-local type; manual split required");
        }

        var directiveSafety = DirectiveAnalyzer.AnalyzeDirectiveSafety(root, types);
        if (!directiveSafety.IsSafe)
        {
            return FileResult.Skip(originalPath, readPath, directiveSafety.Reason ?? "contains unsafe directive; manual split required");
        }

        var plan = SplitPlanner.BuildPlan(originalPath, readPath, types, planned, directiveNote: directiveSafety.Note);
        if (plan.SkipReason is not null)
        {
            return FileResult.Skip(originalPath, plan.KeptPath, plan.SkipReason);
        }

        if (DirectiveAnalyzer.ProducesEmptyDirectiveShell(root, types, plan))
        {
            return FileResult.Skip(originalPath, plan.KeptPath, "splitting would leave an empty directive shell; manual split required");
        }

        try
        {
            if (phase == Phase.Renames)
            {
                _writer.ApplyRenameOnly(originalPath, originalBytes, plan.KeptPath);
                return FileResult.Split(
                    originalPath,
                    plan.KeptPath,
                    plan.GitMove,
                    plan.Note,
                    plan.Files.Where(f => !f.IsKept).Select(f => new NewFileResult(f.Path, f.Type.Key)).ToArray());
            }

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
            return FileResult.Failed(originalPath, plan.KeptPath, ex.Message);
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
