using System.Text;

namespace AgenticRoslynTool;

/// <summary>
/// Snapshot of a source file's byte-level identity: encoding, byte-order mark (BOM)
/// presence, dominant newline sequence, and whether the file ends with a newline. All
/// outputs are re-emitted with the same properties, which is how the tool avoids
/// gratuitous whole-file diffs on re-serialization.
/// </summary>
internal sealed class EncodedSource
{
    private EncodedSource(string text, Encoding encoding, bool emitPreamble, string newLine, bool hasFinalNewLine)
    {
        Text = text;
        Encoding = encoding;
        EmitPreamble = emitPreamble;
        NewLine = newLine;
        HasFinalNewLine = hasFinalNewLine;
    }

    /// <summary>Decoded source text with any BOM stripped.</summary>
    public string Text { get; }

    /// <summary>The encoding to use when writing outputs. Always the encoding the source was read with.</summary>
    public Encoding Encoding { get; }

    /// <summary>
    /// True when the original file began with a BOM. When true, the tool re-emits the
    /// preamble; when false, it does not, so a BOM-less file stays BOM-less.
    /// </summary>
    public bool EmitPreamble { get; }

    /// <summary>The dominant newline sequence in the source, either <c>"\r\n"</c> or <c>"\n"</c>. Ties (no newlines seen) fall back to <see cref="Environment.NewLine"/>.</summary>
    public string NewLine { get; }

    /// <summary>True when the source ended with a newline. Preserved on output so a final-newline convention is not silently changed.</summary>
    public bool HasFinalNewLine { get; }

    /// <summary>
    /// Detects encoding, BOM, newline style, and final-newline state from the raw bytes,
    /// then decodes the text with any BOM removed.
    /// </summary>
    /// <param name="bytes">The full byte contents of the source file.</param>
    /// <returns>An <see cref="EncodedSource"/> capturing the file's identity.</returns>
    public static EncodedSource FromBytes(byte[] bytes)
    {
        var encoding = DetectEncoding(bytes, out var emitPreamble, out var preambleLength);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var newLine = DetectNewLine(text);
        var hasFinalNewLine = text.EndsWith("\r\n", StringComparison.Ordinal) || text.EndsWith("\n", StringComparison.Ordinal);
        return new EncodedSource(text, encoding, emitPreamble, newLine, hasFinalNewLine);
    }

    private static Encoding DetectEncoding(byte[] bytes, out bool emitPreamble, out int preambleLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            emitPreamble = true;
            preambleLength = 3;
            return new UTF8Encoding(false, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            emitPreamble = true;
            preambleLength = 2;
            return new UnicodeEncoding(false, true, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            emitPreamble = true;
            preambleLength = 2;
            return new UnicodeEncoding(true, true, true);
        }

        emitPreamble = false;
        preambleLength = 0;
        return new UTF8Encoding(false, true);
    }

    private static string DetectNewLine(string text)
    {
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        var lf = text.IndexOf('\n');
        if (crlf >= 0 && (lf < 0 || crlf <= lf))
        {
            return "\r\n";
        }

        return lf >= 0 ? "\n" : Environment.NewLine;
    }
}

