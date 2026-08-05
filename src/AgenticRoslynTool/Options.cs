namespace AgenticRoslynTool;

/// <summary>
/// Parsed command-line options for the <c>split-types</c> verb. Immutable so that the
/// splitter can be trusted not to mutate configuration between phases.
/// </summary>
/// <param name="InputPath">Absolute path to the input CSV (with a <c>file</c> column) or newline-delimited path list.</param>
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
internal sealed record Options(string InputPath, string ManifestPath, string RepoRoot, Phase Phase, string? RequiredHeader, IReadOnlyList<string>? Excludes = null)
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
        string repoRoot = Directory.GetCurrentDirectory();
        var phase = Phase.Content;

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
                    phase = Phase.Plan;
                    break;
                case "--phase":
                    phase = ParsePhase(RequireValue(args, ref i, "--phase"));
                    break;
                case "--require-header":
                    requiredHeader = RequireValue(args, ref i, "--require-header");
                    break;
                case "--exclude":
                    excludes.Add(RequireValue(args, ref i, "--exclude"));
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (input is null)
        {
            PrintUsage();
            throw new ArgumentException("Missing --input.");
        }

        manifest ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)) ?? repoRoot, "sa1402-split-manifest.csv");
        return new Options(Path.GetFullPath(input), Path.GetFullPath(manifest), Path.GetFullPath(repoRoot), phase, requiredHeader, excludes);
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

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: agentic-roslyn-tool split-types --input <csv-or-list> [--repo-root <path>] [--manifest <path>] [--phase plan|renames|content] [--dry-run] [--require-header <text>] [--exclude <path-substring>]...");
    }
}
