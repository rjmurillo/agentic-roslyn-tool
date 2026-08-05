namespace AgenticRoslynTool;

/// <summary>
/// Parsed command-line options for the <c>split-types</c> verb. Immutable so that the
/// splitter can be trusted not to mutate configuration between phases.
/// </summary>
/// <param name="InputPath">
/// Absolute path to a directory to scan, a CSV (with a <c>file</c> column), or a
/// newline-delimited path list. The literal <c>-</c> reads the list from standard input.
/// </param>
/// <param name="ManifestPath">Absolute path to the CSV manifest. Defaults to <c>sa1402-split-manifest.csv</c> next to the input.</param>
/// <param name="RepoRoot">Absolute repository root used as the working directory for <c>git mv</c> calls.</param>
/// <param name="Phase">Which of the <see cref="AgenticRoslynTool.Phase"/> stages to execute.</param>
/// <param name="RequiredHeader">Optional file header to require and prepend to every output file.</param>
/// <param name="Excludes">
/// Path substrings that mark an input as untouchable, for example generated output
/// directories. Matching is case-insensitive and runs against the path with both
/// separators normalized to <c>/</c>, so <c>obj/</c> matches on every platform.
/// Empty by default: the tool ships with no opinion about which directories are generated.
/// </param>
/// <param name="Json">When true, the run prints a single JSON report to standard output instead of CSV.</param>
internal sealed record Options(string InputPath, string ManifestPath, string RepoRoot, Phase Phase, string? RequiredHeader, IReadOnlyList<string>? Excludes = null, bool Json = false)
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
        string? requiredHeader = null;
        var excludes = new List<string>();
        var json = false;
        string repoRoot = Directory.GetCurrentDirectory();
        var phase = Phase.Content;
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
        return new Options(inputPath, Path.GetFullPath(manifest), Path.GetFullPath(repoRoot), phase, requiredHeader, excludes, json);
    }

    /// <summary>The <c>--input</c> value that means "read newline-delimited paths from standard input".</summary>
    public const string StdinPath = "-";

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
}
