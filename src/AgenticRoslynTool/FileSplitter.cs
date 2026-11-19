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
    /// <remarks>
    /// This phase recomputes the plan, and <c>Program</c> writes the result over the manifest.
    /// Left unchecked, that would launder an edit made between plan and renames: the new row
    /// would carry the hash of the edited bytes, and the content phase would find a match. So
    /// when a manifest already exists, a file this run would split must still hash to the
    /// value that manifest recorded for it, or it is refused here and never renamed.
    /// <para>
    /// Every prior row with a hash counts, not only the <c>split</c> ones. A file the plan
    /// skipped can gain a type afterwards, and a file the content phase refused as stale is
    /// written back as <c>skipped</c>. Checking split rows alone would let this phase plan
    /// either one afresh. Only the plan phase starts a new plan.
    /// </para>
    /// <para>
    /// A file the manifest has no row for, a row with no hash, and a run with no manifest at
    /// all have nothing to be held to, and proceed as a fresh plan.
    /// </para>
    /// </remarks>
    private IReadOnlyList<FileResult> RunRenames()
    {
        var priorPlan = File.Exists(_options.ManifestPath)
            ? SplitPlanner.IndexByInputPath(ManifestWriter.Read(_options.ManifestPath).Where(r => r.ContentHash is not null))
            : null;
        var results = BuildResolvedPlan(row => RefuseIfStale(row, priorPlan));
        foreach (var result in results.Where(r => r.Status == "split" && r.GitMove))
        {
            _writer.ApplyRenameOnly(result.OriginalPath, File.ReadAllBytes(result.OriginalPath), result.KeptPath);
        }

        return results;
    }

    /// <summary>Runs the plan for every input path and resolves any cross-file output-name collisions into their qualified form.</summary>
    /// <param name="review">
    /// Applied to each row before collisions are resolved, so a row it turns into a skip
    /// claims no output path and cannot force a qualified name onto another input.
    /// </param>
    private IReadOnlyList<FileResult> BuildResolvedPlan(Func<FileResult, FileResult>? review = null)
    {
        var results = new List<FileResult>();
        foreach (var path in InputSource.ReadRunnableInputs(_options.InputPath).ToArray())
        {
            var row = Process(path, Phase.Plan, null);
            results.Add(review is null ? row : review(row));
        }

        return SplitPlanner.ResolvePlannedOutputCollisions(results);
    }

    /// <summary>
    /// Turns a freshly planned <c>split</c> row into a skip when an existing manifest recorded
    /// different bytes for the same file. See the remarks on <see cref="RunRenames"/>.
    /// </summary>
    /// <remarks>
    /// The refusal keeps the recorded hash, not the hash of the bytes found. The row replaces
    /// the manifest row, and a row carrying the new hash would match on the next run, which
    /// would turn one refusal into permission.
    /// </remarks>
    private static FileResult RefuseIfStale(FileResult row, Dictionary<string, FileResult>? priorPlan)
    {
        if (row.Status != "split"
            || row.ContentHash is null
            || priorPlan is null
            || !priorPlan.TryGetValue(row.OriginalPath, out var prior)
            || prior.ContentHash is null)
        {
            return row;
        }

        var stale = ContentHash.DescribeMismatch(prior.ContentHash, row.ContentHash);

        // The rename has not happened, so the file is still at its original path.
        return stale is null ? row : FileResult.Skip(row.OriginalPath, row.OriginalPath, stale) with { ContentHash = prior.ContentHash };
    }

    /// <summary>
    /// Content phase entry point. Reads the plan manifest, then for each input path
    /// runs <see cref="Process"/> against the planned row. An input path matches a row by
    /// its original path, or by its kept path once the renames phase has moved it (see
    /// <see cref="SplitPlanner.IndexByInputPath"/>). Files not present as <c>split</c> in
    /// the manifest are skipped with a diagnostic reason.
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

        var inputs = InputSource.ReadRunnableInputs(_options.InputPath).ToArray();

        // Read once and keep every row. The split-only view below decides what this run can
        // act on; the full set is what gets preserved, because a skipped or failed row is
        // part of the plan of record and a content run must not quietly drop it.
        //
        // Read already collapses the CSV to one row per path, so nothing re-groups here.
        var manifestPlan = ManifestWriter.Read(_options.ManifestPath, requireContentHash: true);
        var plannedFiles = SplitPlanner.IndexByInputPath(manifestPlan.Where(r => r.Status == "split"));

        var results = new List<FileResult>();
        var applied = new HashSet<string>(PathComparison.Comparer);
        foreach (var path in inputs)
        {
            var inputPath = Path.GetFullPath(path);
            if (!plannedFiles.TryGetValue(inputPath, out var planned))
            {
                results.Add(FileResult.Skip(inputPath, inputPath, NotPlannedReason));
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
            return WithPlannedGitMove(ProcessCore(path, phase, planned), planned);
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

            return WithPlannedGitMove(FileResult.Failed(reported, reported, ex.Message), planned);
        }
    }

    /// <summary>
    /// Restores the pending-rename flag the refusal and failure factories drop. Whether a
    /// rename is owed is a fact about the plan, not about how this run ended, so a row that
    /// forgets it tells the next reader no move is pending for a file that still needs one.
    /// The paths are deliberately left alone: they say where the file is, which a planned
    /// row cannot answer for a run that refused before the rename landed.
    /// </summary>
    private static FileResult WithPlannedGitMove(FileResult result, FileResult? planned) =>
        planned is null || result.Status == "split" ? result : result with { GitMove = planned.GitMove };

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
                return FileResult.Skip(originalPath, readPath, stale) with { ContentHash = planned.ContentHash };
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
