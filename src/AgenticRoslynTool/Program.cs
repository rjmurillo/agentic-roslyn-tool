using AgenticRoslynTool;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var verb = args[0];
if (verb is "--help" or "-h" or "help")
{
    PrintUsage();
    return 0;
}

if (verb != "split-types")
{
    Console.Error.WriteLine($"Unknown command: {verb}");
    PrintUsage();
    return 1;
}

var verbArgs = args.Skip(1).ToArray();
var options = Options.Parse(verbArgs);
var splitter = new FileSplitter(options);
var results = splitter.Run();
ManifestWriter.Write(results, options.ManifestPath);

if (options.Phase == Phase.Plan)
{
    Console.WriteLine(ManifestWriter.ToCsv(results));
}
else
{
    Console.WriteLine($"Manifest: {options.ManifestPath}");
}

return results.Any(r => r.Status.StartsWith("failed", StringComparison.OrdinalIgnoreCase)) ? 1 : 0;

static void PrintUsage()
{
    Console.WriteLine("Usage: AgenticRoslynTool <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  split-types   Split C# files that declare more than one top-level type (SA1402).");
    Console.WriteLine();
    Console.WriteLine("split-types options:");
    Console.WriteLine("  --input <csv-or-list>            Path to a CSV with a 'file' column, or a text file with one path per line.");
    Console.WriteLine("  --repo-root <path>               Repository root used for git operations. Defaults to the current directory.");
    Console.WriteLine("  --manifest <path>                Path to the manifest CSV. Defaults to sa1402-split-manifest.csv next to --input.");
    Console.WriteLine("  --phase plan|renames|content     Which phase to run. Defaults to content.");
    Console.WriteLine("  --dry-run                        Shorthand for --phase plan.");
    Console.WriteLine("  --require-header <text>          Require and prepend this file header. Off by default.");
    Console.WriteLine("  --help, -h                       Show help.");
}

