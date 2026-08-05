using System.Reflection;

using AgenticRoslynTool;

// Exit codes are part of the agent-facing contract:
//   0  ran, nothing failed
//   1  ran, at least one manifest row failed
//   2  the command line was wrong
//   3  the run could not start or could not finish
if (args.Length == 0)
{
    Console.Error.WriteLine("error: no command given.");
    PrintUsage(Console.Error);
    return 2;
}

var verb = args[0];
if (verb is "--help" or "-h" or "help")
{
    PrintUsage(Console.Out);
    return 0;
}

if (verb is "--version")
{
    Console.WriteLine(ReadVersion());
    return 0;
}

if (verb != "split-types")
{
    Console.Error.WriteLine($"error: unknown command '{verb}'.");
    PrintUsage(Console.Error);
    return 2;
}

// Handled here rather than inside Options.Parse so that asking for help or for the version
// never depends on the rest of the command line being valid, and so parsing never
// terminates the process. Both are accepted in either position for the same reason.
var rest = args.Skip(1).ToArray();
if (Options.HasMetaOption(rest, "--help", "-h", "help"))
{
    PrintUsage(Console.Out);
    return 0;
}

if (Options.HasMetaOption(rest, "--version"))
{
    Console.WriteLine(ReadVersion());
    return 0;
}

Options options;
try
{
    options = Options.Parse(rest);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    PrintUsage(Console.Error);
    return 2;
}

try
{
    var outcome = new FileSplitter(options).Run();
    ManifestWriter.Write(outcome.ManifestRows, options.ManifestPath);
    var report = RunReport.Create(outcome.ReportRows, options.Phase, options.ManifestPath);

    if (options.Json)
    {
        Console.WriteLine(report.ToJson());
    }
    else if (options.Phase == Phase.Plan)
    {
        Console.WriteLine(ManifestWriter.ToCsv(outcome.ManifestRows));
    }

    // Standard output carries a parseable document or nothing at all, so a caller can pipe
    // it straight into a parser. The manifest path and the summary are progress reporting,
    // which belongs on standard error.
    Console.Error.WriteLine($"Manifest: {options.ManifestPath}");
    Console.Error.WriteLine(report.ToSummaryLine());
    return report.Summary.Failed > 0 ? 1 : 0;
}
// Unfiltered on purpose. This is the process boundary, and the contract an agent depends on
// is that a failed run emits one "error:" line and exit 3, never a stack trace and never
// exit -532462766. A filter here only holds until some path throws a type nobody listed:
// a manifest with a duplicate header name, for instance, throws ArgumentException out of
// ManifestWriter.Read. The stack trace is still available to a human through the exception
// message plus the failing input, which is what the summary and manifest already carry.
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 3;
}

static string ReadVersion()
{
    // InformationalVersion carries the full package version including any prerelease
    // suffix, which AssemblyVersion drops. SourceLink appends "+<sha>"; trim it.
    var informational = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    return informational?.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";
}

static void PrintUsage(TextWriter w)
{
    w.WriteLine("Usage: agentic-roslyn-tool <command> [options]");
    w.WriteLine();
    w.WriteLine("Commands:");
    w.WriteLine("  split-types   Split C# files that declare more than one top-level type (SA1402).");
    w.WriteLine();
    w.WriteLine("split-types options:");
    w.WriteLine("  --input <dir|file|csv|list|->    A directory to scan, one .cs file, a CSV with a 'file' column,");
    w.WriteLine("                                   a text file with one path per line, or '-' to read paths from stdin.");
    w.WriteLine("  --repo-root <path>               Repository root used for git operations. Defaults to the current directory.");
    w.WriteLine("  --manifest <path>                Path to the manifest CSV. Defaults to sa1402-split-manifest.csv beside a list");
    w.WriteLine("                                   input, or in --repo-root for a directory or stdin input.");
    w.WriteLine("  --phase plan|renames|content     Which phase to run. Defaults to content.");
    w.WriteLine("  --dry-run                        Shorthand for --phase plan. Cannot be combined with --phase.");
    w.WriteLine("  --json                           Print one JSON report to stdout instead of CSV.");
    w.WriteLine("  --require-header <text>          Require and prepend this file header. Off by default.");
    w.WriteLine("  --exclude <path-substring>       Skip inputs whose path contains this substring. Repeatable.");
    w.WriteLine("  --help, -h                       Show help.");
    w.WriteLine();
    w.WriteLine("Exit codes: 0 no failures, 1 a file failed, 2 bad command line, 3 the run could not complete.");
    w.WriteLine("Standard output carries the data (CSV or JSON); a one-line summary goes to standard error.");
}
