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
/// written and is recorded as <c>failed</c> in the run report.
/// </para>
/// </remarks>
internal sealed class FileSplitter
{
    private readonly Options _options;

    private readonly WorkspaceWriter _writer;

    /// <summary>Reason recorded when the content phase is handed a file the plan never covered.</summary>
    private const string NotPlannedReason = "not present as split in plan manifest";

    /// <summary>
    /// Reason recorded when a content run meets a row an earlier content run already applied.
    /// The words say what was checked and nothing more. The leading <c>already applied</c> is
    /// published: callers match on it to tell this skip from a refusal, so it must not change.
    /// </summary>
    private const string AlreadyAppliedReason = "already applied: every planned output exists and this file no longer declares the planned types";

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
    /// <exception cref="InvalidOperationException">Thrown if the phase value is unrecognized, or, in the content phase, if the plan manifest is missing.</exception>
    public IReadOnlyList<FileResult> Run()
    {
        return _options.Phase switch
        {
            Phase.Plan => RunPlan(),
            Phase.Renames => RunRenames(),
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
    /// The phase neither reads nor writes the plan manifest. It recomputes because a rename
    /// has to follow the file as it is now, and the rows it returns go to the run report.
    /// </remarks>
    private IReadOnlyList<FileResult> RunRenames()
    {
        return BuildResolvedPlan().Select(ApplyPlannedRename).ToArray();
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

        try
        {
            _writer.ApplyRenameOnly(planned.OriginalPath, File.ReadAllBytes(planned.OriginalPath), planned.KeptPath);
            return planned;
        }
        catch (Exception ex)
        {
            // The file is still at its original path, and the rename is still owed.
            return FileResult.Failed(planned.OriginalPath, planned.OriginalPath, ex.Message) with { GitMove = true };
        }
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
    /// before the report is written, leaving changed files and no record of which. Per-file
    /// failures inside the loop are handled the same way for the same reason: <see cref="Process"/>
    /// turns any failure on one input, expected or not, into a row rather than an abort, so the
    /// report describes what the run actually did. That holds as far as the report write
    /// itself; a run whose <c>ManifestWriter.Write</c> fails still loses the record, and nothing
    /// inside this method can prevent that.
    /// </remarks>
    private IReadOnlyList<FileResult> RunContent()
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
        var manifestPlan = ManifestWriter.Read(_options.ManifestPath);
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

        // A planned split the current input never mentioned is reported as a skip, so a
        // caller applying content in batches sees what is still owed. The plan itself is
        // untouched on disk, so the next batch reads the same rows.
        var unapplied = manifestPlan
            .Where(p => p.Status == "split" && !applied.Contains(p.OriginalPath))
            .Select(p => FileResult.Skip(
                p.OriginalPath,
                p.KeptPath,
                "planned as split but not supplied to the content phase input"));

        return results.Concat(unapplied).ToArray();
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
    /// report at the end, so anything that escapes this method strands every file already
    /// rewritten with no record of them. A filtered catch only holds until some path throws a
    /// type nobody listed, and this method reaches an inconsistent rename state, a hand-edited
    /// manifest, and the file system. One bad input costs a failed row; one escaped exception
    /// costs the record of the whole run.
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
            // rewritten in this run with no report recording them.
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
            return FileResult.Failed(originalPath, readPath, "input has syntax errors: " + OutputVerifier.JoinDiagnostics(originalDiagnostics));
        }

        if (root.Members.OfType<GlobalStatementSyntax>().Any())
        {
            return FileResult.Skip(originalPath, readPath, "contains top-level statements; manual split required");
        }

        var types = TopLevelType.Find(root).ToArray();
        if (phase == Phase.Content && planned is not null && SplitPlanner.IsAlreadyApplied(planned, types))
        {
            return FileResult.Skip(originalPath, readPath, AlreadyAppliedReason);
        }

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
