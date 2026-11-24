namespace AgenticRoslynTool;

/// <summary>
/// Finds the MSBuild files under a repository root, and says which of them the scan may
/// open. Repository content is untrusted, so anything unusual is reported, never skipped.
/// </summary>
internal static class MsBuildFileFinder
{
    /// <summary>The largest MSBuild file read, 16 MiB. A project that lists every file is a few megabytes at most.</summary>
    internal const long MaxFileBytes = 16L * 1024 * 1024;

    private static readonly string[] Extensions = [".csproj", ".props", ".targets", ".projitems", ".shproj"];

    /// <summary>
    /// Every <c>.csproj</c>, <c>.props</c>, <c>.targets</c>, <c>.projitems</c>, and
    /// <c>.shproj</c> file under <paramref name="root"/>, plus every directory symlink that
    /// leads outside it. Directories named <c>bin</c>, <c>obj</c>, and <c>.git</c> are skipped.
    /// </summary>
    /// <remarks>
    /// No directory symlink is followed. One whose final target lies under the root needs no
    /// visit, because the walk reaches the same files by their real path. One that leads
    /// outside the root, or nowhere, is returned: it can hold a project the scan does not
    /// read, so the caller treats it like an MSBuild file it cannot open. Walks the tree by
    /// hand for the reason <see cref="InputSource.DiscoverSourceFiles"/> does.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a directory cannot be listed. A partial scan would let a split through that a missed project forbids.</exception>
    internal static List<string> Find(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                found.AddRange(Directory.EnumerateFiles(directory).Where(IsMsBuildFile));
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var link = new DirectoryInfo(child);
                    if (link.LinkTarget is null)
                    {
                        pending.Push(child);
                    }
                    else if (!IsInside(link.ResolveLinkTarget(returnFinalTarget: true)?.FullName, root))
                    {
                        found.Add(child);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // The advice keeps the root where it is. A narrower root is a partial scan.
                throw new InvalidOperationException(
                    $"cannot scan {directory} for MSBuild files: {ex.Message.TrimEnd('.')}. Make that directory readable; --repo-root must stay the repository root.",
                    ex);
            }
        }

        return found;
    }

    /// <summary>
    /// False for a path the scan will not open: a symlink, an empty file, or one over
    /// <see cref="MaxFileBytes"/>. The caller treats each as unreadable, which refuses
    /// inputs and never lets one through.
    /// </summary>
    /// <remarks>
    /// A symlink can point outside the repository or at a file of any size. A FIFO or a
    /// device reports a length of zero, and opening one can block the run forever. An empty
    /// file is not valid XML either, so the zero length check costs nothing real.
    /// </remarks>
    internal static bool IsSafeToOpen(string path)
    {
        var file = new FileInfo(path);
        return file.LinkTarget is null && file.Length is > 0 and <= MaxFileBytes;
    }

    /// <summary>
    /// True when a link's final target lies under the root, so the walk reads those files
    /// through their real path. Case counts, so a doubtful match is reported, not skipped.
    /// </summary>
    private static bool IsInside(string? target, string root)
    {
        if (target is null)
        {
            return false;
        }

        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsMsBuildFile(string path) =>
        Extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
