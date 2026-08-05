namespace AgenticRoslynTool;

/// <summary>
/// Resolves the <c>--input</c> argument into the list of files a run will process.
/// </summary>
/// <remarks>
/// Split out of <see cref="FileSplitter"/> because deciding which files to look at has
/// nothing to do with how a file is split. Everything here is static and reads only the
/// argument it is given, so each input shape can be exercised without constructing a run.
/// </remarks>
internal static class InputSource
{
    /// <summary>
    /// Streams input paths from a directory (every <c>.cs</c> file beneath it), from a
    /// single <c>.cs</c> file, from standard input when the path is <c>-</c>, from a CSV
    /// file (which must contain a <c>file</c> column), or from a newline-delimited text
    /// file. Empty lines and empty <c>file</c> values are skipped.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the CSV input has no <c>file</c> column.</exception>
    internal static IEnumerable<string> ReadInputs(string inputPath)
    {
        if (inputPath == Options.StdinPath)
        {
            return ReadLinePaths(ReadStandardInputLines());
        }

        if (Directory.Exists(inputPath))
        {
            return DiscoverSourceFiles(inputPath);
        }

        // A .cs file is the file to split, not a list of paths. Reading it as a list would
        // feed every line of C# in as a path and report a screen of "input file does not
        // exist" skips at exit code 0, which reads like success.
        if (Path.GetExtension(inputPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return [inputPath];
        }

        if (Path.GetExtension(inputPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return ReadCsvPaths(inputPath);
        }

        return ReadLinePaths(File.ReadLines(inputPath));
    }

    internal static IEnumerable<string> ReadStandardInputLines()
    {
        while (Console.In.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// Enumerates every <c>.cs</c> file beneath a directory, skipping <c>bin</c> and
    /// <c>obj</c> segments. Those hold compiler and generator output that a build
    /// recreates, so splitting them would churn files nobody reads. That is a fact
    /// about how .NET lays out a build rather than an opinion about one repository,
    /// which is the line decision 14 draws around <c>--exclude</c>.
    /// </summary>
    /// <remarks>
    /// Walks the tree by hand rather than using <see cref="EnumerationOptions"/>. That
    /// type's <c>AttributesToSkip</c> applies to files as well as directories, so skipping
    /// reparse points there would silently drop a symlinked source file, which is a second
    /// undocumented exclusion. Here only directory symlinks are cut, which is what stops a
    /// link pointing at an ancestor from recursing forever.
    /// </remarks>
    internal static IEnumerable<string> DiscoverSourceFiles(string root)
    {
        // The whole list is materialized before any file is processed, so an unreadable
        // directory fails the run before the content phase has rewritten anything.
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly));

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    var isBuildOutput = name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase);
                    var isLink = new DirectoryInfo(child).LinkTarget is not null;

                    if (!isBuildOutput && !isLink)
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Not swallowed. A partial scan exits 0 and looks exactly like a scan that
                // found no work, which is the one outcome an agent cannot recover from.
                throw new InvalidOperationException(
                    $"cannot scan directory {directory}: {ex.Message}. Pass a list file instead of a directory to skip the unreadable part.",
                    ex);
            }
        }

        // A file symlink and its target can both be inside the scanned tree. They are two
        // paths naming one file, and processing both would apply the split twice: the second
        // pass sees an already-split file and reports nothing to do, so one planned output is
        // silently attributed to the wrong path. De-duplicate by symlink target, and keep the
        // real path rather than the link so the manifest names the file git tracks. Aliases
        // that are not reparse points, hard links and bind mounts, are not detected.
        return files
            .Select(f => (Path: f, Identity: PhysicalIdentity(f)))
            .OrderBy(t => !PathComparison.Comparer.Equals(t.Path, t.Identity))
            .ThenBy(t => t.Path, StringComparer.Ordinal)
            .DistinctBy(t => t.Identity, PathComparison.Comparer)
            .Select(t => t.Path)
            .ToArray();
    }

    /// <summary>
    /// Resolves a path to the file it ultimately names, so a symlink and its target compare
    /// equal. Falls back to the path itself when the link cannot be resolved, which errs
    /// toward processing the file rather than dropping it.
    /// </summary>
    internal static string PhysicalIdentity(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    internal static IEnumerable<string> ReadCsvPaths(string inputPath)
    {
        using var parser = new CsvFieldReader(inputPath);
        var header = parser.ReadFields() ?? Array.Empty<string>();
        var fileIndex = Array.FindIndex(header, h => h.Equals("file", StringComparison.OrdinalIgnoreCase));
        if (fileIndex < 0)
        {
            throw new InvalidOperationException("CSV must contain a file column.");
        }

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is not null && fields.Length > fileIndex && !string.IsNullOrWhiteSpace(fields[fileIndex]))
            {
                yield return fields[fileIndex];
            }
        }
    }

    internal static IEnumerable<string> ReadLinePaths(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length != 0)
            {
                yield return trimmed;
            }
        }
    }

    /// <summary>
    /// De-duplicates the input list. Every surviving path produces a manifest row, including
    /// excluded ones. Comparison uses <see cref="PathComparison.Comparer"/> so that
    /// de-duplication and the content phase's manifest lookup agree on what counts as the
    /// same file.
    /// </summary>
    /// <remarks>
    /// Paths are resolved to absolute form before de-duplication, not after. A caller can
    /// list the same file as <c>src/Foo.cs</c> and as <c>C:\repo\src\Foo.cs</c>, and the two
    /// strings differ, so de-duplicating first let one file be split twice. The consumers
    /// resolve to the same absolute path a moment later, which is what makes the duplicate
    /// invisible until it has already rewritten a file.
    /// </remarks>
    internal static IEnumerable<string> ReadRunnableInputs(string inputPath)
    {
        return ReadInputs(inputPath).Select(Path.GetFullPath).Distinct(PathComparison.Comparer);
    }
}
