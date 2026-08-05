using System.Text;

namespace AgenticRoslynTool;

/// <summary>
/// Hand-written CSV field reader that supports the comma delimiter, quoted fields,
/// doubled-quote escaping, and embedded newlines inside quoted values. Used for both
/// reading the input list and the plan manifest.
/// </summary>
/// <remarks>
/// This exists as a deliberate replacement for
/// <c>Microsoft.VisualBasic.FileIO.TextFieldParser</c>. TextFieldParser requires the
/// Windows Desktop targeting pack, which would force a Windows-only target framework
/// on this tool. A future agent may be tempted to swap it back or simplify it. Do not:
/// it must keep parity on the four features above.
/// </remarks>
internal sealed class CsvFieldReader : IDisposable
{
    private readonly StreamReader _reader;
    private bool _endOfData;

    /// <summary>Opens the file for reading, detecting encoding from any BOM.</summary>
    /// <param name="path">Absolute path to the CSV file.</param>
    public CsvFieldReader(string path)
    {
        _reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        _endOfData = _reader.EndOfStream;
    }

    /// <summary>True once every record in the file has been returned.</summary>
    public bool EndOfData => _endOfData;

    /// <summary>
    /// Reads and returns the next record as an array of field values, or null when the
    /// file has no more records.
    /// </summary>
    /// <returns>The parsed field values, or null at end of file.</returns>
    public string[]? ReadFields()
    {
        if (_endOfData)
        {
            return null;
        }

        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var recordHasContent = false;

        while (true)
        {
            var next = _reader.Read();
            if (next < 0)
            {
                if (inQuotes || recordHasContent || current.Length > 0 || fields.Count > 0)
                {
                    fields.Add(current.ToString());
                    _endOfData = true;
                    return fields.ToArray();
                }

                _endOfData = true;
                return null;
            }

            var ch = (char)next;

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (_reader.Peek() == '"')
                    {
                        _reader.Read();
                        current.Append('"');
                        continue;
                    }

                    inQuotes = false;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch == '"' && current.Length == 0)
            {
                inQuotes = true;
                recordHasContent = true;
                continue;
            }

            if (ch == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
                recordHasContent = true;
                continue;
            }

            if (ch == '\r')
            {
                if (_reader.Peek() == '\n')
                {
                    _reader.Read();
                }

                fields.Add(current.ToString());
                _endOfData = _reader.EndOfStream;
                return fields.ToArray();
            }

            if (ch == '\n')
            {
                fields.Add(current.ToString());
                _endOfData = _reader.EndOfStream;
                return fields.ToArray();
            }

            current.Append(ch);
            recordHasContent = true;
        }
    }

    /// <summary>Releases the underlying stream reader.</summary>
    public void Dispose()
    {
        _reader.Dispose();
    }
}
