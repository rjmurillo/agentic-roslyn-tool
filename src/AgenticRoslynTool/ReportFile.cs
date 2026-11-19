namespace AgenticRoslynTool;

/// <summary>
/// Proves the run report can be written, and that writing it is safe, before a renames or
/// content run changes anything.
/// </summary>
/// <remarks>
/// The report is written once, after every source file is rewritten. A path that turns out
/// to be unwritable at that point costs the record of the whole run, so every refusal here
/// happens first. The report path is caller supplied, so it is also the one write in the
/// tool that could land on a file the caller cares about. Three files are protected: the
/// plan manifest, however the path reaches it, and any existing file that is not a report
/// from an earlier run, which covers the input list and every source file.
/// </remarks>
internal static class ReportFile
{
    /// <summary>The first column of the manifest and report format. A file that starts with it is safe to replace.</summary>
    private const string HeaderStart = "originalPath,";

    /// <summary>
    /// Checks the report path and clears the previous report. After this returns, no report
    /// exists at the path, so a run that aborts cannot leave an earlier run's rows behind
    /// for a caller to mistake for its own.
    /// </summary>
    /// <param name="reportPath">Absolute path the run will write its report to.</param>
    /// <param name="manifestPath">Absolute path of the plan manifest.</param>
    /// <exception cref="InvalidOperationException">Thrown when the path reaches the manifest, names a directory, names a file that is not a report, or cannot be written.</exception>
    public static void Prepare(string reportPath, string manifestPath)
    {
        // Collision checks are case insensitive on every platform, like the others in this
        // tool. Wrongly refusing costs one failed start. Wrongly allowing costs the plan.
        if (StringComparer.OrdinalIgnoreCase.Equals(ResolveLink(reportPath), ResolveLink(manifestPath)))
        {
            throw new InvalidOperationException($"The report path {reportPath} reaches the plan manifest. The report would overwrite the reviewed plan. Pass a different --report path.");
        }

        if (Directory.Exists(reportPath))
        {
            throw new InvalidOperationException($"The report path {reportPath} is a directory. Pass a file path to --report.");
        }

        try
        {
            if (File.Exists(reportPath) && !IsReplaceable(reportPath))
            {
                throw new InvalidOperationException($"Refusing to overwrite {reportPath}. It exists and is not a report from an earlier run. Pass a different --report path.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            using (new FileStream(reportPath, FileMode.Create, FileAccess.Write))
            {
            }

            File.Delete(reportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot write the report at {reportPath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Follows a symbolic link to the file it names. A path that is not a link, or does not
    /// exist yet, is its own answer. Hard links and links in a parent directory are not
    /// followed, which is a stated limit.
    /// </summary>
    private static string ResolveLink(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
    }

    /// <summary>True for an empty file and for a file in the manifest and report format.</summary>
    private static bool IsReplaceable(string path)
    {
        var firstLine = File.ReadLines(path).FirstOrDefault();
        return firstLine is null || firstLine.TrimStart('"').StartsWith(HeaderStart, StringComparison.Ordinal);
    }
}
