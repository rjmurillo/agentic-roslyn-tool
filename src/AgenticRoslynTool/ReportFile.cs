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
/// Three rules keep that write safe. Nothing here opens an existing file for writing: the
/// probe and the report both go to a new sibling file, and the report is then moved into
/// place. The move only replaces a file in the report format. And whether the report path
/// is the manifest is observed, not derived from the two paths, see
/// <see cref="ReachesManifest"/>. Every check runs before the run and again at the write,
/// because the run creates files.
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
    /// <exception cref="InvalidOperationException">Thrown when the manifest path is a link, or the report path is the manifest, is a link or a directory, names a file that is not a report, or sits in a directory that cannot be written.</exception>
    public static void Prepare(string reportPath, string manifestPath)
    {
        string? refusal;
        try
        {
            refusal = Refusal(reportPath, manifestPath);
            if (refusal is null)
            {
                // A new file beside the report path proves the directory takes one, and
                // shows whether that directory is the manifest's. The report path itself
                // is never opened for writing.
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
                var probe = SiblingPath(reportPath);
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
                {
                }

                try
                {
                    refusal = ReachesManifest(reportPath, probe, manifestPath) ? ManifestRefusal(reportPath) : null;
                }
                finally
                {
                    File.Delete(probe);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot write the report at {reportPath}: {ex.Message}", ex);
        }

        if (refusal is not null)
        {
            throw new InvalidOperationException(refusal);
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
            if (refusal is null)
            {
                ManifestWriter.Write(rows, staged);
                refusal = ReachesManifest(reportPath, staged, manifestPath) ? ManifestRefusal(reportPath) : null;
            }

            if (refusal is not null)
            {
                File.Delete(staged);
                return refusal;
            }

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
        // A linked manifest names a file somewhere else, and finding that somewhere means
        // resolving the link by hand. Three versions of this type did that and each got a
        // case wrong. Refusing the link leaves nothing to resolve. Also true of a dangling
        // link, which File.Exists reports as absent.
        if (new FileInfo(manifestPath).LinkTarget is not null)
        {
            return $"The manifest path {manifestPath} is a symbolic link. Pass the path of the file it names to --manifest.";
        }

        if (Path.EndsInDirectorySeparator(reportPath) || Directory.Exists(reportPath))
        {
            return $"The report path {reportPath} is a directory. Pass a file path to --report.";
        }

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

    private static string ManifestRefusal(string reportPath) =>
        $"The report path {reportPath} is the plan manifest under another name. The report would overwrite the reviewed plan. Pass a different --report path.";

    /// <summary>
    /// True when moving a file onto <paramref name="reportPath"/> would replace the plan
    /// manifest: the two sit in one directory under one name.
    /// </summary>
    /// <remarks>
    /// Whether two paths name one directory is observed, not worked out. A file this run
    /// just created beside the report path is visible under the manifest's directory
    /// exactly when the two are the same directory, whatever links either path runs
    /// through. Two earlier versions resolved the links by hand, and each missed a chain
    /// the operating system follows. The name comparison ignores case on every platform,
    /// like the other collision checks: wrongly refusing costs one failed start, and
    /// wrongly allowing costs the plan. A hard link to the manifest is a different
    /// directory entry, and replacing it leaves the manifest alone.
    /// </remarks>
    /// <param name="reportPath">Absolute path of the report. Never a link, see <see cref="Refusal"/>.</param>
    /// <param name="sibling">An existing file this run created in the report's directory.</param>
    /// <param name="manifestPath">Absolute path of the plan manifest. Never a link, see <see cref="Refusal"/>.</param>
    private static bool ReachesManifest(string reportPath, string sibling, string manifestPath)
    {
        // Neither path is resolved here. The operating system follows the manifest's
        // directory as typed when it looks for the sibling.
        var manifestDirectory = Path.GetDirectoryName(manifestPath) ?? ".";
        return File.Exists(Path.Combine(manifestDirectory, Path.GetFileName(sibling)))
            && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(reportPath), Path.GetFileName(manifestPath));
    }

    private static bool IsReportFormat(string path)
    {
        var firstLine = File.ReadLines(path).FirstOrDefault();
        return firstLine is not null && firstLine.StartsWith(FirstColumn + ",", StringComparison.Ordinal);
    }

    /// <summary>
    /// A path in the report's directory that nothing else uses. The name does not embed the
    /// report's name, so a long report name cannot push it past the file name limit.
    /// </summary>
    private static string SiblingPath(string reportPath) =>
        Path.Combine(Path.GetDirectoryName(reportPath) ?? ".", Guid.NewGuid().ToString("N") + ".tmp");
}
