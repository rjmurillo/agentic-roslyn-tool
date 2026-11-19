namespace AgenticRoslynTool;

/// <summary>
/// Owns the run report file: proves the path is usable before a renames or content run
/// changes anything, and writes the report afterwards without replacing a file that is not
/// a report.
/// </summary>
/// <remarks>
/// <para>
/// The report is written once, after every source file is rewritten. A path that turns out
/// to be unusable at that point ends a run that already changed files with exit code 3 and
/// no report file, so <see cref="Prepare"/> refuses first. The report path is also caller
/// supplied, which makes it the one write in the tool that could land on a file the caller
/// cares about.
/// </para>
/// <para>
/// Two rules keep that write safe. Nothing here opens an existing file for writing: the
/// probe and the report both go to a new sibling file, and the report is then moved into
/// place. And the move only replaces a file in the report format, checked again at the
/// moment of the move, because the run may have created a source file at that path since
/// the first check.
/// </para>
/// </remarks>
internal static class ReportFile
{
    /// <summary>The first column of the manifest and report format.</summary>
    private const string FirstColumn = "originalPath";

    /// <summary>
    /// Refuses a report path the run could not safely write to. Changes no existing file:
    /// an earlier report stays where it is until <see cref="Write"/> replaces it. It does
    /// create the report's directory when that is missing, and leaves it.
    /// </summary>
    /// <param name="reportPath">Absolute path the run will write its report to.</param>
    /// <param name="manifestPath">Absolute path of the plan manifest.</param>
    /// <exception cref="InvalidOperationException">Thrown when the path reaches the manifest, is a link or a directory, names a file that is not a report, or sits in a directory that cannot be written.</exception>
    public static void Prepare(string reportPath, string manifestPath)
    {
        try
        {
            var refusal = Refusal(reportPath, manifestPath);
            if (refusal is not null)
            {
                throw new InvalidOperationException(refusal);
            }

            // Prove the directory takes a new file, without touching the report path.
            var directory = Path.GetDirectoryName(reportPath) ?? ".";
            Directory.CreateDirectory(directory);
            var probe = SiblingPath(reportPath);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
            {
            }

            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot write the report at {reportPath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Writes the report. The rows go to a new file beside the report path, which is then
    /// moved into place, so a reader never sees half a report and no existing file is ever
    /// opened for writing.
    /// </summary>
    /// <param name="rows">The rows describing what the run did.</param>
    /// <param name="reportPath">Absolute path of the report.</param>
    /// <param name="manifestPath">Absolute path of the plan manifest.</param>
    /// <returns>Null when the report was written. Otherwise the reason it was not, and nothing at the report path has changed.</returns>
    public static string? Write(IReadOnlyList<FileResult> rows, string reportPath, string manifestPath)
    {
        var staged = SiblingPath(reportPath);
        try
        {
            // Asked again on purpose. The run has written files since Prepare, and one of
            // them may now sit at this path.
            var refusal = Refusal(reportPath, manifestPath);
            if (refusal is not null)
            {
                return refusal;
            }

            ManifestWriter.Write(rows, staged);
            File.Move(staged, reportPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(staged);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The staged file is a leftover beside the report. It is not worth hiding
                // the failure that matters behind a second one.
            }

            return ex.Message;
        }
    }

    /// <summary>
    /// Says why the report must not be written at <paramref name="reportPath"/>, or returns
    /// null when it may. A path nothing occupies is fine. So is a file in the manifest and
    /// report format, which is what an earlier report is.
    /// </summary>
    private static string? Refusal(string reportPath, string manifestPath)
    {
        // Collision checks are case insensitive on every platform, like the others in this
        // tool. Wrongly refusing costs one failed start. Wrongly allowing costs the plan.
        if (StringComparer.OrdinalIgnoreCase.Equals(RealPath(reportPath), RealPath(manifestPath)))
        {
            return $"The report path {reportPath} reaches the plan manifest. The report would overwrite the reviewed plan. Pass a different --report path.";
        }

        if (Path.EndsInDirectorySeparator(reportPath) || Directory.Exists(reportPath))
        {
            return $"The report path {reportPath} is a directory. Pass a file path to --report.";
        }

        // Covers a dangling link too, which File.Exists reports as absent.
        if (new FileInfo(reportPath).LinkTarget is not null)
        {
            return $"The report path {reportPath} is a symbolic link. Pass the path of a regular file to --report.";
        }

        if (File.Exists(reportPath) && !IsReportFormat(reportPath))
        {
            return $"Refusing to overwrite {reportPath}. It exists and is not a report from an earlier run. Pass a different --report path.";
        }

        return null;
    }

    private static bool IsReportFormat(string path)
    {
        var firstLine = File.ReadLines(path).FirstOrDefault();
        return firstLine is not null && firstLine.StartsWith(FirstColumn + ",", StringComparison.Ordinal);
    }

    /// <summary>A path beside the report that nothing else uses.</summary>
    private static string SiblingPath(string reportPath) => reportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

    /// <summary>
    /// Resolves symbolic links in every component of a path, so two spellings of one file
    /// compare equal. Components that do not exist yet are kept as written. Hard links are
    /// not resolved, and do not need to be: <see cref="Write"/> replaces the directory
    /// entry at the report path and never writes through it.
    /// </summary>
    /// <remarks>
    /// One link is replaced per pass, and the result is resolved again from the root. A
    /// link's target can itself run through a linked directory, and an earlier version that
    /// took each target as final let such a path reach the manifest unseen.
    /// </remarks>
    private static string RealPath(string path)
    {
        var current = Path.GetFullPath(path);
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            var next = ReplaceFirstLink(current);
            if (next is null)
            {
                return current;
            }

            current = next;
        }

        throw new IOException($"Too many levels of symbolic links: {path}");
    }

    /// <summary>The usual operating system bound on links followed while resolving one path.</summary>
    private const int MaxLinkHops = 40;

    /// <summary>
    /// Returns <paramref name="fullPath"/> with its first linked component replaced by that
    /// link's own target, or null when no component is a link.
    /// </summary>
    private static string? ReplaceFirstLink(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var parts = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var resolved = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = Path.Combine(resolved, parts[i]);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (info.LinkTarget is { } target)
            {
                // A relative target is relative to the directory holding the link, and every
                // component of that directory is already link free.
                var rest = Path.Combine(parts[(i + 1)..]);
                return Path.GetFullPath(Path.Combine(Path.GetFullPath(target, resolved), rest));
            }

            resolved = candidate;
        }

        return null;
    }
}
