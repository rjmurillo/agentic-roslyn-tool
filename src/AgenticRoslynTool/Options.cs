namespace AgenticRoslynTool;

/// <summary>
/// Parsed command-line options for the <c>split-types</c> verb. Immutable so that the
/// splitter can be trusted not to mutate configuration between phases.
/// </summary>
/// <param name="InputPath">
/// Absolute path to a directory to scan, a CSV (with a <c>file</c> column), or a
/// newline-delimited path list. The literal <c>-</c> reads the list from standard input.
/// </param>
/// <param name="ManifestPath">Absolute path to the CSV plan manifest. Defaults to <c>sa1402-split-manifest.csv</c> next to the input. Only the plan phase writes it.</param>
/// <param name="RepoRoot">Absolute repository root used as the working directory for <c>git mv</c> calls.</param>
/// <param name="Phase">
/// Which of the <see cref="AgenticRoslynTool.Phase"/> stages to execute. <see cref="Parse"/>
/// defaults it to <see cref="Phase.Plan"/>, so only an explicit <c>--phase</c> changes source.
/// </param>
/// <param name="RequiredHeader">Optional file header to require and prepend to every output file.</param>
/// <param name="Excludes">
/// Path substrings that mark an input as untouchable, for example generated output
/// directories. Matching is case-insensitive and runs against the path with both
/// separators normalized to <c>/</c>, so <c>obj/</c> matches on every platform.
/// Empty by default: the tool ships with no opinion about which directories are generated.
/// </param>
/// <param name="Json">When true, the run prints a single JSON report to standard output instead of CSV.</param>
/// <param name="ReportPath">
/// Absolute path the renames and content phases write their run report to. <see cref="Parse"/>
/// always fills it and rejects the manifest path. Code that builds the record directly may
/// leave it null, which means the default beside the manifest, see
/// <see cref="DefaultReportPath"/>. <see cref="ReportFile.Prepare"/> tests it against the
/// disk before a run, whoever built the options.
/// </param>
/// <param name="CollisionNaming">
/// How two outputs that collide across input files are qualified, or null when
/// <c>--collision-naming</c> was absent. Null is not <see cref="CollisionNaming.FileFirst"/>:
/// the plan phase reads it as file-first, and the renames and content phases read it as
/// "use the value the plan recorded". See <see cref="CollisionNamingFlag.Resolve"/>.
/// </param>
internal sealed record Options(string InputPath, string ManifestPath, string RepoRoot, Phase Phase, string? RequiredHeader, IReadOnlyList<string>? Excludes = null, bool Json = false, string? ReportPath = null, CollisionNaming? CollisionNaming = null)
{
    /// <summary>
    /// Parses the raw argument array into an <see cref="Options"/> instance, resolving
    /// relative paths to absolute paths and defaulting <see cref="ManifestPath"/> when
    /// it was not supplied. Exits the process on <c>--help</c>.
    /// </summary>
    /// <param name="args">Command-line arguments after the verb.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="ArgumentException">Thrown when a required argument is missing, an unknown argument is present, or a value flag is supplied without a value.</exception>
    public static Options Parse(string[] args)
    {
        string? input = null;
        string? manifest = null;
        string? report = null;
        string? requiredHeader = null;
        var excludes = new List<string>();
        var json = false;
        CollisionNaming? collisionNaming = null;
        string repoRoot = Directory.GetCurrentDirectory();
        // Plan, never content. A caller that has not read the docs, which is the usual agent,
        // runs the command with no --phase first. That first call must change no source file.
        var phase = Phase.Plan;
        var dryRun = false;
        var phaseGiven = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input":
                    input = RequireValue(args, ref i, "--input");
                    break;
                case "--manifest":
                    manifest = RequireValue(args, ref i, "--manifest");
                    break;
                case "--report":
                    report = RequireValue(args, ref i, "--report");
                    break;
                case "--repo-root":
                    repoRoot = RequireValue(args, ref i, "--repo-root");
                    break;
                case "--dry-run":
                    dryRun = true;
                    phase = Phase.Plan;
                    break;
                case "--phase":
                    phaseGiven = true;
                    phase = ParsePhase(RequireValue(args, ref i, "--phase"));
                    break;
                case "--require-header":
                    requiredHeader = RequireValue(args, ref i, "--require-header");
                    break;
                case "--exclude":
                    excludes.Add(RequireValue(args, ref i, "--exclude"));
                    break;
                case "--json":
                    json = true;
                    break;
                case "--collision-naming":
                    collisionNaming = ParseCollisionNaming(RequireValue(args, ref i, "--collision-naming"));
                    break;

                // --help is handled by the caller before parsing, so asking for help never
                // depends on the rest of the command line. Parsing must not print or exit:
                // a library that calls Environment.Exit cannot be tested or hosted.
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (input is null)
        {
            throw new ArgumentException("Missing --input.");
        }

        // Both set the phase, so the last one on the line used to win silently. That turned
        // argument order into the difference between writing a manifest and rewriting source.
        if (dryRun && phaseGiven)
        {
            throw new ArgumentException("--dry-run and --phase cannot be combined. --dry-run is shorthand for --phase plan.");
        }

        var inputPath = input == StdinPath ? StdinPath : Path.GetFullPath(input);
        manifest ??= Path.Combine(DefaultManifestDirectory(inputPath, repoRoot), "sa1402-split-manifest.csv");
        // A trailing separator survives every path call below and hides the file name from
        // any comparison of names. "plan.csv/" and "plan.csv" would then read as two files.
        if (Path.EndsInDirectorySeparator(manifest))
        {
            throw new ArgumentException("--manifest must name a file. Remove the trailing directory separator.");
        }

        var manifestPath = Path.GetFullPath(manifest);

        // The plan phase writes the manifest and no report, so the flag would do nothing
        // there. A flag that is accepted and ignored reads as success to a caller that
        // cannot see the screen.
        if (report is not null && phase == Phase.Plan)
        {
            throw new ArgumentException("--report applies to the renames and content phases. The plan phase writes the manifest.");
        }

        // The report must never land on the plan. That overwrite is the defect the report
        // file exists to remove: a failed row would replace its own plan and block a retry.
        var reportPath = report is null ? DefaultReportPath(manifestPath) : Path.GetFullPath(report);
        // Case insensitive on every platform, like the other collision checks. Wrongly
        // rejecting costs one exit 2. Wrongly accepting costs the plan.
        if (StringComparer.OrdinalIgnoreCase.Equals(reportPath, manifestPath))
        {
            throw new ArgumentException("--report and --manifest cannot name the same file. The report would overwrite the reviewed plan.");
        }

        return new Options(inputPath, manifestPath, Path.GetFullPath(repoRoot), phase, requiredHeader, excludes, json, reportPath, collisionNaming);
    }

    /// <summary>
    /// Where the run report lands when <c>--report</c> is absent: beside the manifest, with
    /// <c>.report.csv</c> in place of the manifest's extension. Deriving it from the manifest
    /// name means the default can never be the manifest itself.
    /// </summary>
    /// <param name="manifestPath">Absolute path of the plan manifest.</param>
    public static string DefaultReportPath(string manifestPath) => Path.ChangeExtension(manifestPath, ".report.csv");

    /// <summary>The <c>--input</c> value that means "read newline-delimited paths from standard input".</summary>
    public const string StdinPath = "-";

    /// <summary>Options that consume the token after them, so that token is a value and never a flag.</summary>
    private static readonly string[] ValueOptions =
        ["--input", "--manifest", "--report", "--repo-root", "--phase", "--require-header", "--exclude", "--collision-naming"];

    /// <summary>
    /// True when any of <paramref name="names"/> appears in an option position.
    /// </summary>
    /// <remarks>
    /// Scanning every token instead would make <c>--input help</c> print usage and exit 0
    /// rather than looking for a file named <c>help</c>, which is a silent no-op for a
    /// caller that cannot see the screen. Lives here because <see cref="ValueOptions"/>
    /// has to stay in step with the switch in <see cref="Parse"/>.
    /// </remarks>
    /// <param name="args">Command-line arguments after the verb.</param>
    /// <param name="names">The meta-option spellings to look for.</param>
    public static bool HasMetaOption(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (Array.IndexOf(ValueOptions, args[i]) >= 0)
            {
                i++;
                continue;
            }

            if (Array.IndexOf(names, args[i]) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Chooses where an unspecified manifest lands: beside a list or CSV input, and in the
    /// repository root when the input is a directory or standard input. A scanned directory
    /// holds the caller's source, so writing the manifest into it would dirty the tree the
    /// run is about to report on.
    /// </summary>
    private static string DefaultManifestDirectory(string inputPath, string repoRoot)
    {
        if (inputPath == StdinPath || Directory.Exists(inputPath))
        {
            return repoRoot;
        }

        return Path.GetDirectoryName(inputPath) ?? repoRoot;
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} requires a value.");
        }

        index++;
        return args[index];
    }

    private static Phase ParsePhase(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "plan" => Phase.Plan,
            "renames" => Phase.Renames,
            "content" => Phase.Content,
            _ => throw new ArgumentException($"Unknown phase: {value}"),
        };
    }

    private static CollisionNaming ParseCollisionNaming(string value)
    {
        return CollisionNamingFlag.TryParse(value, out var collisionNaming)
            ? collisionNaming
            : throw new ArgumentException($"Unknown collision naming: {value}. Use file-first or type-first.");
    }
}
