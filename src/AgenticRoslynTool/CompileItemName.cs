using System.Text;
using System.Text.RegularExpressions;

namespace AgenticRoslynTool;

/// <summary>
/// Reads the file name part of one MSBuild <c>Compile</c> item value and says which file
/// names it could match. Works on text only. Nothing here evaluates MSBuild.
/// </summary>
/// <remarks>
/// <para>
/// Only the segment after the last <c>/</c> or <c>\</c> is read, escaped or not. Directory
/// parts are ignored on purpose: the tool does not evaluate properties, so it cannot tell
/// which directory a value names. A file of the same name in another directory is refused
/// too. See decision 31.
/// </para>
/// <para>
/// A reference, <c>$(...)</c>, <c>@(...)</c>, or <c>%(...)</c>, can expand to any text,
/// so it is read as a wildcard. A separator inside one is not a segment boundary.
/// <c>$(MSBuildThisFileDirectory)Foo.cs</c> therefore reads as any name that ends in
/// <c>Foo.cs</c>, which matches <c>Foo.cs</c> itself.
/// </para>
/// <para>
/// A value is read one of three ways, tried in order: an exact name, a suffix after
/// leading references, or a pattern. The first two are looked up, so a project that lists
/// thousands of files costs a few lookups per input, not one match per item.
/// </para>
/// </remarks>
internal static class CompileItemName
{
    /// <summary>The literal file name <paramref name="value"/> names, or null when its segment holds a wildcard or a reference.</summary>
    /// <param name="value">One <c>;</c>-separated value of an <c>Include</c>, <c>Update</c>, <c>Remove</c>, or <c>Exclude</c> attribute, already trimmed.</param>
    internal static string? ExactName(string value)
    {
        var segment = FileSegment(value);
        return segment.Length == 0 || HasWildcard(segment, 0) ? null : MsBuildText.Unescape(segment);
    }

    /// <summary>
    /// The literal text after the leading references of the segment, when references are its
    /// only wildcards. <c>$(Dir)Foo.cs</c> gives <c>Foo.cs</c>: any name ending in it matches.
    /// </summary>
    internal static string? Suffix(string value)
    {
        var segment = FileSegment(value);
        var tail = 0;
        while (tail < segment.Length && MsBuildText.IsReferenceStart(segment, tail))
        {
            tail = MsBuildText.ReferenceEnd(segment, tail) + 1;
        }

        return tail == 0 || HasWildcard(segment, tail) ? null : MsBuildText.Unescape(segment[tail..]);
    }

    /// <summary>
    /// Returns the matcher for the segment of <paramref name="value"/>, or null when the
    /// segment names nothing or names every file in its directory.
    /// </summary>
    /// <remarks>
    /// A segment of exactly <c>*.cs</c>, <c>*</c>, or <c>**</c> returns null. Such an item
    /// matches the new sibling files a split writes as well as the original, so the split
    /// leaves the set of compiled types unchanged.
    /// </remarks>
    internal static Regex? Matcher(string value)
    {
        if (NamesNoSingleFile(value))
        {
            return null;
        }

        var segment = FileSegment(value);

        // The text comes from files in the repository, so the engine must not backtrack. A
        // pattern of many wildcards would otherwise take exponential time on one name.
        return new Regex(
            "^" + ToPattern(segment) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
    }

    /// <summary>True when the segment is empty, or exactly <c>*.cs</c>, <c>*</c>, or <c>**</c>. Cheap: no pattern is built.</summary>
    internal static bool NamesNoSingleFile(string value)
    {
        var segment = FileSegment(value);
        return segment.Length == 0 || segment is "*" or "**" || segment.Equals("*.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The text after the last separator outside every reference. An escaped separator,
    /// <c>%2F</c> or <c>%5C</c>, counts: MSBuild unescapes it before it reads the path.
    /// </summary>
    private static string FileSegment(string value)
    {
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (MsBuildText.IsReferenceStart(value, i))
            {
                i = MsBuildText.ReferenceEnd(value, i);
            }
            else if (value[i] is '/' or '\\')
            {
                start = i + 1;
            }
            else if (MsBuildText.TryReadEscape(value, i, out var escaped) && escaped is '/' or '\\')
            {
                start = i + 3;
                i += 2;
            }
        }

        return value[start..];
    }

    private static bool HasWildcard(string segment, int from)
    {
        for (var i = from; i < segment.Length; i++)
        {
            if (segment[i] is '*' or '?' || MsBuildText.IsReferenceStart(segment, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a regular expression body for one segment. A wildcard or a reference matches
    /// any run of characters, and <c>?</c> matches one. A run of wildcards compiles to one.
    /// </summary>
    private static string ToPattern(string segment)
    {
        var pattern = new StringBuilder();
        var afterWildcard = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var isReference = MsBuildText.IsReferenceStart(segment, i);
            if (isReference || segment[i] == '*')
            {
                i = isReference ? MsBuildText.ReferenceEnd(segment, i) : i;
                pattern.Append(afterWildcard ? string.Empty : ".*");
                afterWildcard = true;
                continue;
            }

            afterWildcard = false;
            if (segment[i] == '?')
            {
                pattern.Append('.');
            }
            else if (MsBuildText.TryReadEscape(segment, i, out var escaped))
            {
                pattern.Append(Regex.Escape(escaped.ToString()));
                i += 2;
            }
            else
            {
                pattern.Append(Regex.Escape(segment[i].ToString()));
            }
        }

        return pattern.ToString();
    }
}
