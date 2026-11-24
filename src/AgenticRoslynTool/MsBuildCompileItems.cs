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
    /// <summary>The longest item value read, in characters. A longer one makes its file unreadable, so no pattern is built from it.</summary>
    internal const int MaxValueLength = 4096;

    /// <summary>
    /// The most wildcard patterns one scan keeps. Each input is matched against every one,
    /// so the cap bounds that cost. A file whose pattern finds no room counts as unreadable.
    /// </summary>
    internal const int MaxPatterns = 10_000;

    private static readonly string[] ItemAttributes = ["Include", "Update", "Remove", "Exclude"];

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MsBuildFileFinder.MaxFileBytes,
    };

    // Each store is filled in ordinal file order, and the first file to add a key keeps it.
    private readonly Dictionary<string, string> _exactNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _suffixes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (Regex Pattern, string File)> _patterns = [];

    private readonly List<(string Directory, string File)> _unreadable = [];

    private MsBuildCompileItems()
    {
    }

    /// <summary>Reason recorded for an input a <c>Compile</c> item names.</summary>
    /// <param name="file">The MSBuild file, relative to the repository root, with <c>/</c> separators.</param>
    internal static string NamedReason(string file) => $"file is named by an MSBuild Compile item in {file}; manual split required";

    /// <summary>Reason recorded for an input in or below the directory of an MSBuild file, or a directory symlink, the scan could not read.</summary>
    /// <param name="file">The unreadable path, relative to the repository root, with <c>/</c> separators.</param>
    internal static string UnreadableReason(string file) => $"cannot read MSBuild Compile items in {file}; manual split required";

    /// <summary>Reads every path <see cref="MsBuildFileFinder.Find"/> returns for <paramref name="repoRoot"/>.</summary>
    /// <remarks>
    /// Files are read in ordinal order of their relative path, so the file a reason names is
    /// the same on every run and every platform. A path <see cref="MsBuildFileFinder.IsSafeToOpen"/>
    /// rejects, a file that is not valid XML or cannot be read, and a file with an item value
    /// over <see cref="MaxValueLength"/> or a pattern past <see cref="MaxPatterns"/> are
    /// recorded as unreadable. Names read from a file
    /// before its error still count, which only refuses more.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a directory cannot be listed.</exception>
    internal static MsBuildCompileItems Scan(string repoRoot)
    {
        var items = new MsBuildCompileItems();
        var files = MsBuildFileFinder.Find(repoRoot)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(repoRoot, path).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);

        foreach (var (path, relative) in files)
        {
            try
            {
                if (!MsBuildFileFinder.IsSafeToOpen(path) || !items.Read(path, relative))
                {
                    items._unreadable.Add((Path.GetDirectoryName(path)!, relative));
                }
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
    /// the way. A named refusal wins over an unreadable one.
    /// </summary>
    /// <param name="paths">Every full path the input is known by: where it was planned, and where it is read now.</param>
    internal string? DescribeRefusal(params string[] paths)
    {
        // Every file that names the input, from all three stores. The earliest is reported.
        var named = paths
            .Select(path => Path.GetFileName(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(name => Enumerable.Range(0, name.Length + 1)
                .Select(start => _suffixes.GetValueOrDefault(name[start..]))
                .Append(_exactNames.GetValueOrDefault(name))
                .Concat(_patterns.Values.Where(p => p.Pattern.IsMatch(name)).Select(p => p.File)))
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

        if (named is not null)
        {
            return NamedReason(named);
        }

        // Case is ignored, so a refusal errs toward the safe side on every platform.
        var unreadable = _unreadable.FirstOrDefault(u => paths.Any(path =>
            path.StartsWith(u.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        return unreadable.File is null ? null : UnreadableReason(unreadable.File);
    }

    /// <summary>
    /// Reads one file. Returns false when a value was too long to read safely, or the scan
    /// already holds <see cref="MaxPatterns"/> patterns. Reading goes on either way, so
    /// later names in the file still count.
    /// </summary>
    private bool Read(string path, string relative)
    {
        var complete = true;
        using var reader = XmlReader.Create(path, ReaderSettings);
        while (reader.Read())
        {
            // MSBuild reads item types without regard to case, so <compile> counts too.
            if (reader.NodeType != XmlNodeType.Element || !reader.LocalName.Equals("Compile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Attribute names are matched in any case too. That only reads more values.
            while (reader.MoveToNextAttribute())
            {
                if (!ItemAttributes.Contains(reader.LocalName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in reader.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    complete &= value.Length <= MaxValueLength && Add(value, relative);
                }
            }
        }

        return complete;
    }

    /// <summary>Stores one value. Returns false when it is a pattern the scan has no room for.</summary>
    private bool Add(string value, string relative)
    {
        if (CompileItemName.ExactName(value) is { } exact)
        {
            _exactNames.TryAdd(exact, relative);
        }
        else if (CompileItemName.Suffix(value) is { } suffix)
        {
            _suffixes.TryAdd(suffix, relative);
        }
        else if (_patterns.Count >= MaxPatterns)
        {
            return CompileItemName.NamesNoSingleFile(value);
        }
        else if (CompileItemName.Matcher(value) is { } pattern)
        {
            _patterns.TryAdd(pattern.ToString(), (pattern, relative));
        }

        return true;
    }
}
