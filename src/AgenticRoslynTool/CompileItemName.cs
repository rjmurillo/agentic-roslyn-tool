using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgenticRoslynTool;

/// <summary>
/// Reads the file name part of one MSBuild <c>Compile</c> item value and says which file
/// names it could match. Works on text only. Nothing here evaluates MSBuild.
/// </summary>
/// <remarks>
/// <para>
/// Only the segment after the last <c>/</c> or <c>\</c> is read. Directory parts are
/// ignored on purpose: the tool does not evaluate properties, so it cannot tell which
/// directory a value names. A file of the same name in another directory is refused too.
/// See decision 31.
/// </para>
/// <para>
/// A property, item, or metadata reference, <c>$(...)</c>, <c>@(...)</c>, or
/// <c>%(...)</c>, can expand to any text, so it is read as a wildcard. A separator inside
/// one is not a segment boundary. <c>$(MSBuildThisFileDirectory)Foo.cs</c> therefore reads
/// as any name that ends in <c>Foo.cs</c>, which matches <c>Foo.cs</c> itself.
/// </para>
/// </remarks>
internal static class CompileItemName
{
    /// <summary>
    /// Returns the matcher for the file name segment of <paramref name="value"/>, or null
    /// when the segment can name nothing or names every file in its directory.
    /// </summary>
    /// <remarks>
    /// A segment of exactly <c>*.cs</c>, <c>*</c>, or <c>**</c> returns null. Such an item
    /// matches the new sibling files a split writes as well as the original, so the split
    /// leaves the set of compiled types unchanged.
    /// </remarks>
    /// <param name="value">One <c>;</c>-separated value of an <c>Include</c>, <c>Update</c>, <c>Remove</c>, or <c>Exclude</c> attribute, already trimmed.</param>
    internal static Regex? Matcher(string value)
    {
        var segment = FileSegment(value);
        if (segment.Length == 0 || IsWholeDirectory(segment))
        {
            return null;
        }

        // The text comes from files in the repository, so the engine must not backtrack. A
        // pattern of many wildcards would otherwise take exponential time on one name.
        return new Regex(
            "^" + ToPattern(segment) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
    }

    /// <summary>
    /// The literal file name <paramref name="value"/> names, or null when its file name
    /// segment holds a wildcard or a reference.
    /// </summary>
    /// <remarks>
    /// Exact names go into a dictionary, so a project that lists thousands of files costs one
    /// lookup per input and not one match per item.
    /// </remarks>
    internal static string? ExactName(string value)
    {
        var segment = FileSegment(value);
        return segment.Length == 0 || HasReference(segment) || segment.IndexOfAny(['*', '?']) >= 0
            ? null
            : Unescape(segment);
    }

    /// <summary>The text after the last separator that sits outside every reference.</summary>
    private static string FileSegment(string value)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (IsReferenceStart(value, i))
            {
                depth++;
                i++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
            else if ((c == '/' || c == '\\') && depth == 0)
            {
                start = i + 1;
            }
        }

        return value[start..];
    }

    private static bool IsWholeDirectory(string segment) =>
        segment is "*" or "**" || segment.Equals("*.cs", StringComparison.OrdinalIgnoreCase);

    private static bool HasReference(string segment)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            if (IsReferenceStart(segment, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReferenceStart(string text, int index) =>
        index + 1 < text.Length && text[index + 1] == '(' && text[index] is '$' or '@' or '%';

    /// <summary>
    /// Builds a regular expression body for one segment. A wildcard or a reference matches
    /// any run of characters, <c>?</c> matches one, and <c>%XX</c> is MSBuild's escape for
    /// one literal character.
    /// </summary>
    private static string ToPattern(string segment)
    {
        var pattern = new StringBuilder();
        var afterWildcard = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var isReference = IsReferenceStart(segment, i);
            if (isReference || segment[i] == '*')
            {
                if (isReference)
                {
                    i = SkipReference(segment, i);
                }

                // A run of wildcards matches what one does, so it compiles to one.
                if (!afterWildcard)
                {
                    pattern.Append(".*");
                }

                afterWildcard = true;
                continue;
            }

            afterWildcard = false;
            if (segment[i] == '?')
            {
                pattern.Append('.');
            }
            else if (TryReadEscape(segment, i, out var escaped))
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

    /// <summary>Returns the index of the parenthesis that closes the reference at <paramref name="start"/>, or the last index when it never closes.</summary>
    private static int SkipReference(string segment, int start)
    {
        var depth = 0;
        for (var i = start + 1; i < segment.Length; i++)
        {
            if (segment[i] == '(')
            {
                depth++;
            }
            else if (segment[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return segment.Length - 1;
    }

    private static string Unescape(string segment)
    {
        var text = new StringBuilder();
        for (var i = 0; i < segment.Length; i++)
        {
            if (TryReadEscape(segment, i, out var escaped))
            {
                text.Append(escaped);
                i += 2;
            }
            else
            {
                text.Append(segment[i]);
            }
        }

        return text.ToString();
    }

    private static bool TryReadEscape(string text, int index, out char escaped)
    {
        escaped = default;
        if (text[index] != '%' || index + 2 >= text.Length
            || !byte.TryParse(text.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            return false;
        }

        escaped = (char)code;
        return true;
    }
}
