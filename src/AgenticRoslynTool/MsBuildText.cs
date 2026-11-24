using System.Globalization;
using System.Text;

namespace AgenticRoslynTool;

/// <summary>
/// The two pieces of MSBuild item syntax the scan must read: references, written
/// <c>$(...)</c>, <c>@(...)</c>, or <c>%(...)</c>, and escapes, written <c>%XX</c>.
/// </summary>
internal static class MsBuildText
{
    /// <summary>True when a reference starts at <paramref name="index"/>.</summary>
    internal static bool IsReferenceStart(string text, int index) =>
        index + 1 < text.Length && text[index + 1] == '(' && text[index] is '$' or '@' or '%';

    /// <summary>
    /// Returns the index of the parenthesis that closes the reference at
    /// <paramref name="start"/>, or the last index when it never closes.
    /// </summary>
    /// <remarks>
    /// Every parenthesis inside counts, so a property function call such as
    /// <c>$([System.IO.Path]::Combine('a', 'b'))</c> ends at its last parenthesis.
    /// </remarks>
    internal static int ReferenceEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    /// <summary>Reads the <c>%XX</c> escape at <paramref name="index"/>, when one is there.</summary>
    internal static bool TryReadEscape(string text, int index, out char escaped)
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

    /// <summary>Replaces every <c>%XX</c> escape with the character it stands for.</summary>
    internal static string Unescape(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (TryReadEscape(text, i, out var escaped))
            {
                result.Append(escaped);
                i += 2;
            }
            else
            {
                result.Append(text[i]);
            }
        }

        return result.ToString();
    }
}
