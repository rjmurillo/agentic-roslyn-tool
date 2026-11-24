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
    /// <c>.shproj</c> file under <paramref name="root"/>, plus every directory symlink whose
    /// target the walk does not reach. Directories named <c>bin</c>, <c>obj</c>, and
    /// <c>.git</c> are skipped.
    /// </summary>
    /// <remarks>
    /// No directory symlink is followed. One whose final target the walk reaches by its real
    /// path needs no visit. Any other is returned: a target outside the root, or one inside
    /// a skipped directory. It can hold a project the scan does not read, so the caller
    /// treats it like an MSBuild file it cannot open. Walks the tree by hand for the reason
    /// <see cref="InputSource.DiscoverSourceFiles"/> does.
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
                    if (IsSkipped(Path.GetFileName(child)))
                    {
                        continue;
                    }

                    var link = new DirectoryInfo(child);
                    if (link.LinkTarget is null)
                    {
                        pending.Push(child);
                    }
                    else if (!IsWalked(link.ResolveLinkTarget(returnFinalTarget: true)?.FullName, root))
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
    /// True when the walk reads a link's final target by its real path: the target lies
    /// under the root, and no directory on the way down is one the walk skips. Case counts
    /// for the root, so a doubtful match is reported, not skipped.
    /// </summary>
    private static bool IsWalked(string? target, string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return target is not null
            && target.StartsWith(prefix, StringComparison.Ordinal)
            && !target[prefix.Length..].Split(Path.DirectorySeparatorChar).Any(IsSkipped);
    }

    private static bool IsSkipped(string name) =>
        name.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    private static bool IsMsBuildFile(string path) =>
        Extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
