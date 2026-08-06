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
    private IReadOnlyList<FileResult> RunRenames()
    {
        var results = BuildResolvedPlan();
        foreach (var result in results.Where(r => r.Status == "split" && r.GitMove))
        {
            _writer.ApplyRenameOnly(result.OriginalPath, File.ReadAllBytes(result.OriginalPath), result.KeptPath);
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

        var inputs = InputSource.ReadRunnableInputs(_options.InputPath).ToArray();

        // Read once and keep every row. The split-only view below decides what this run can
        // act on; the full set is what gets preserved, because a skipped or failed row is
        // part of the plan of record and a content run must not quietly drop it.
        //
        // Read already collapses the CSV to one row per path, so nothing re-groups here.
        var manifestPlan = ManifestWriter.Read(_options.ManifestPath);
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
    /// <para>
    /// A non-split outcome does not get to restate where the file lives. Most refusal sites
    /// answer with the original path and no git move, which is right while the plan is still
    /// being made and wrong once a rename has landed: the manifest would point a reader at a
    /// path the renames phase already emptied. When a planned row exists, its location wins.
    /// </para>
    /// </remarks>
    private FileResult Process(string path, Phase phase, FileResult? planned)
    {
        var result = ProcessOrFail(path, phase, planned);
        return planned is null || result.Status == "split"
            ? result
            : result with { KeptPath = planned.KeptPath, GitMove = planned.GitMove };
    }

    private FileResult ProcessOrFail(string path, Phase phase, FileResult? planned)
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
    /// unexpected throw, funnels through the one guard in <see cref="ProcessOrFail"/>.
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
            return FileResult.Failed(originalPath, originalPath, "input has syntax errors: " + OutputVerifier.JoinDiagnostics(originalDiagnostics));
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

        var directiveSafety = DirectiveAnalyzer.AnalyzeDirectiveSafety(root, types);
        if (!directiveSafety.IsSafe)
        {
            return FileResult.Skip(originalPath, originalPath, directiveSafety.Reason ?? "contains unsafe directive; manual split required");
        }

        var plan = SplitPlanner.BuildPlan(originalPath, readPath, types, planned, directiveSafety.Note);
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
