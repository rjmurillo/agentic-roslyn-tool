using System.Text.RegularExpressions;
using System.Xml;

namespace AgenticRoslynTool;

/// <summary>
/// Every MSBuild <c>Compile</c> item under the repository root that names a file, read
/// once per run. Answers whether a split of one input could change what a project compiles.
/// </summary>
/// <remarks>
/// <para>
/// A split writes new sibling files. A project that names the input in a <c>Compile</c>
/// item does not compile those siblings: a linked <c>Include</c> from another project, a
/// shared project's <c>.projitems</c>, or an old-style project that lists every file. A
/// <c>Remove</c> or <c>Exclude</c> now compiles them where the original was left out. An
/// <c>Update</c> gives the original metadata the siblings lack. Any of these changes the
/// build, so such an input is refused before anything is planned. See decision 31.
/// </para>
/// <para>
/// The files are parsed as XML only. No property is evaluated and no import is followed,
/// so decision 1 still holds: the tool loads no project and needs no build.
/// </para>
/// </remarks>
internal sealed class MsBuildCompileItems
{
    private static readonly string[] Extensions = [".csproj", ".props", ".targets", ".projitems", ".shproj"];

    private static readonly string[] ItemAttributes = ["Include", "Update", "Remove", "Exclude"];

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    // Exact names are looked up, wildcards are matched. A project that lists every file can
    // hold thousands of exact names and almost no wildcards.
    private readonly Dictionary<string, string> _exactNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(Regex Pattern, string File)> _patterns = [];

    private readonly List<(string Directory, string File)> _unreadable = [];

    private MsBuildCompileItems()
    {
    }

    /// <summary>Reason recorded for an input a <c>Compile</c> item names.</summary>
    /// <param name="file">The MSBuild file, relative to the repository root, with <c>/</c> separators.</param>
    internal static string NamedReason(string file) => $"file is named by an MSBuild Compile item in {file}; manual split required";

    /// <summary>Reason recorded for an input in or below the directory of an MSBuild file that could not be read as XML.</summary>
    /// <param name="file">The MSBuild file, relative to the repository root, with <c>/</c> separators.</param>
    internal static string UnreadableReason(string file) => $"cannot read MSBuild Compile items in {file}; manual split required";

    /// <summary>
    /// Reads every <c>.csproj</c>, <c>.props</c>, <c>.targets</c>, <c>.projitems</c>, and
    /// <c>.shproj</c> file under <paramref name="repoRoot"/>. Directories named <c>bin</c>,
    /// <c>obj</c>, and <c>.git</c> are skipped, and directory symlinks are not followed.
    /// </summary>
    /// <remarks>
    /// Files are read in ordinal order of their relative path, so the file a reason names is
    /// the same on every run and every platform.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a directory cannot be listed. A partial scan would let a split through that a missed project forbids.</exception>
    internal static MsBuildCompileItems Scan(string repoRoot)
    {
        var items = new MsBuildCompileItems();
        var files = FindMsBuildFiles(repoRoot)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(repoRoot, path).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);

        foreach (var (path, relative) in files)
        {
            try
            {
                items.Read(path, relative);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                items._unreadable.Add((Path.GetDirectoryName(path)!, relative));
            }
        }

        return items;
    }

    /// <summary>
    /// Returns the reason an input must not be split, or null when no MSBuild file stands in
    /// the way.
    /// </summary>
    /// <param name="paths">Every full path the input is known by: where it was planned, and where it is read now.</param>
    internal string? DescribeRefusal(params string[] paths)
    {
        // Both stores were filled in ordinal file order, so the first hit in each is that
        // store's earliest file, and the smaller of the two is the earliest of all.
        string? named = null;
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (_exactNames.TryGetValue(name, out var file))
            {
                named = Earliest(named, file);
            }

            var match = _patterns.FirstOrDefault(p => p.Pattern.IsMatch(name));
            if (match.File is not null)
            {
                named = Earliest(named, match.File);
            }
        }

        if (named is not null)
        {
            return NamedReason(named);
        }

        // Case is ignored, so a refusal errs toward the safe side on every platform.
        var unreadable = _unreadable.FirstOrDefault(u => paths.Any(path =>
            path.StartsWith(u.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        return unreadable.File is null ? null : UnreadableReason(unreadable.File);
    }

    private static string Earliest(string? current, string candidate) =>
        current is null || string.CompareOrdinal(candidate, current) < 0 ? candidate : current;

    private void Read(string path, string relative)
    {
        using var reader = XmlReader.Create(path, ReaderSettings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Compile")
            {
                continue;
            }

            foreach (var attribute in ItemAttributes)
            {
                var text = reader.GetAttribute(attribute);
                if (text is null)
                {
                    continue;
                }

                foreach (var value in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Add(value, relative);
                }
            }
        }
    }

    private void Add(string value, string relative)
    {
        if (CompileItemName.ExactName(value) is { } exact)
        {
            _exactNames.TryAdd(exact, relative);
        }
        else if (CompileItemName.Matcher(value) is { } pattern)
        {
            _patterns.Add((pattern, relative));
        }
    }

    /// <summary>Walks the tree by hand for the reason <see cref="InputSource.DiscoverSourceFiles"/> does.</summary>
    private static List<string> FindMsBuildFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory).Where(IsMsBuildFile));
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    var skipped = name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(".git", StringComparison.OrdinalIgnoreCase);
                    if (!skipped && new DirectoryInfo(child).LinkTarget is null)
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new InvalidOperationException(
                    $"cannot scan {directory} for MSBuild files: {ex.Message.TrimEnd('.')}. Pass --repo-root to name the repository's own root.",
                    ex);
            }
        }

        return files;
    }

    private static bool IsMsBuildFile(string path) =>
        Extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
