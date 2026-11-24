using System.Reflection;

using AgenticRoslynTool;

// Exit codes are part of the agent-facing contract:
//   0  ran, nothing failed
//   1  ran, at least one row failed
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

// Bare "help" counts only as the first token after the verb. Anywhere else it is a stray
// positional that should fail as a bad command line, because "--json help" printing usage
// and exiting 0 is the same silent successful no-op that "--input help" used to be.
if ((rest.Length > 0 && rest[0] == "help") || Options.HasMetaOption(rest, "--help", "-h"))
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
    // Only the plan phase writes the manifest. It is the reviewed plan, and a later phase
    // that wrote its results there would replace a failed row's plan and block the retry.
    // The renames and content phases record what they did in the report file instead.
    //
    // The report path is proven before the run, not after. The report is written once
    // every source file is rewritten, which is too late to learn the path is unusable.
    string? reportPath = null;
    if (options.Phase != Phase.Plan)
    {
        reportPath = options.ReportPath ?? Options.DefaultReportPath(options.ManifestPath);
        ReportFile.Prepare(reportPath, options.ManifestPath);
    }

    var rows = new FileSplitter(options).Run(out var collisionNaming);

    // A report that cannot be written is not allowed to take the record of the run with
    // it. The path was usable a moment ago, so a failure here is a disk filling, a
    // permission changing, or the run itself creating a source file at the report path.
    // The rows go to standard output instead, and the run ends with exit code 3.
    string? reportFailure = null;
    if (reportPath is null)
    {
        ManifestWriter.Write(rows, options.ManifestPath, collisionNaming);
    }
    else
    {
        reportFailure = ReportFile.Write(rows, reportPath, options.ManifestPath, collisionNaming);
    }

    // A run whose report could not be written did not complete, so it suggests nothing.
    var nextArgs = reportFailure is null ? NextStep.For(options, rows, planOnDisk: File.Exists(options.ManifestPath)) : null;
    var report = RunReport.Create(rows, options.Phase, options.ManifestPath, reportFailure is null ? reportPath : null, collisionNaming, nextArgs);

    if (options.Json)
    {
        Console.WriteLine(report.ToJson());
    }
    else if (options.Phase == Phase.Plan || reportFailure is not null)
    {
        Console.WriteLine(ManifestWriter.ToCsv(rows, collisionNaming));
    }

    // Standard output carries a parseable document or nothing at all, so a caller can pipe
    // it straight into a parser. The file paths and the summary are progress reporting,
    // which belongs on standard error.
    Console.Error.WriteLine($"Manifest: {options.ManifestPath}");
    if (reportPath is not null && reportFailure is null)
    {
        Console.Error.WriteLine($"Report: {reportPath}");
    }

    Console.Error.WriteLine(report.ToSummaryLine());
    if (nextArgs is not null)
    {
        Console.Error.WriteLine(NextStep.Describe(nextArgs, options.Phase));
    }

    if (reportFailure is not null)
    {
        Console.Error.WriteLine($"error: report not written to {reportPath}: {reportFailure}");
        return 3;
    }

    return report.Summary.Failed > 0 ? 1 : 0;
}
// Unfiltered on purpose. This is the process boundary, and the contract an agent depends on
// is that a failed run emits one "error:" line and exit 3, never a stack trace and never
// exit -532462766. A filter here only holds until some path throws a type nobody listed:
// a manifest with a duplicate header name, for instance, throws ArgumentException out of
// ManifestWriter.Read. The stack trace is still available to a human through the exception
// message plus the failing input, which is what the summary and report already carry.
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
    w.WriteLine("  --repo-root <path>               Repository root used for git operations and scanned for MSBuild Compile items.");
    w.WriteLine("                                   Defaults to the current directory.");
    w.WriteLine("  --manifest <path>                Path to the plan manifest CSV. Defaults to sa1402-split-manifest.csv beside a list");
    w.WriteLine("                                   input, or in --repo-root for a directory or stdin input. Only plan writes it.");
    w.WriteLine("                                   Must name a file. renames and content refuse a symbolic link here.");
    w.WriteLine("  --report <path>                  Where renames and content record what they did. Defaults to the manifest path");
    w.WriteLine("                                   with .report.csv as its extension. Not accepted with the plan phase.");
    w.WriteLine("                                   Replaces a file in the report format. Refuses any other existing file.");
    w.WriteLine("  --phase plan|renames|content     Which phase to run. Defaults to plan, which changes no source file.");
    w.WriteLine("  --dry-run                        Same as --phase plan. Kept for older scripts. Cannot be combined with --phase.");
    w.WriteLine("  --json                           Print one JSON report to stdout instead of CSV.");
    w.WriteLine("  --require-header <text>          Require and prepend this file header. Off by default.");
    w.WriteLine("  --exclude <path-substring>       Skip inputs whose path contains this substring. Repeatable.");
    w.WriteLine("  --collision-naming file-first|type-first");
    w.WriteLine("                                   Name for two inputs that emit the same file name. file-first, the default,");
    w.WriteLine("                                   gives A.Shared.cs. type-first gives Shared.A.cs, which passes SA1649.");
    w.WriteLine("                                   The plan records the value; renames and content reuse it when the flag is absent.");
    w.WriteLine("  --file-naming stylecop|metadata  SA1649 convention for generic file names. Defaults to stylecop: Foo.cs,");
    w.WriteLine("                                   or Foo{T}.cs when a same-name type shares the file. metadata names");
    w.WriteLine("                                   them Foo`1.cs. Pass the same value to every phase.");
    w.WriteLine("  --help, -h                       Show help.");
    w.WriteLine();
    w.WriteLine("Typical workflow. Use one list file and one manifest outside the repository for every step:");
    w.WriteLine("  1. split-types --input <list> --manifest <csv> --json                    plan; changes no source file");
    w.WriteLine("  2. split-types --input <list> --manifest <csv> --json --phase renames    git mv only; commit it");
    w.WriteLine("  3. split-types --input <list> --manifest <csv> --json --phase content    rewrites files; build and test");
    w.WriteLine("  With --json, nextArgs in the report holds the exact arguments for the next step.");
    w.WriteLine();
    w.WriteLine("Exit codes: 0 no failures, 1 a file failed, 2 bad command line, 3 the run could not complete.");
    w.WriteLine("Exit 0 can still mean nothing happened. Read summary.split, or the summary line.");
    w.WriteLine("Standard output carries the data (CSV or JSON); the summary and a Next: line go to standard error.");
    w.WriteLine("If the report file cannot be written, its rows go to standard output and the run exits 3.");
}
