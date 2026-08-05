using System.Text;

namespace AgenticRoslynTool;

/// <summary>
/// Reads and writes the CSV manifest that ties the three phases of the split-types
/// workflow together. The plan phase writes it, the renames and content phases read
/// it. Column order and header names are load-bearing: the content phase looks them
/// up by name, and the row-per-new-file layout is what lets one input file expand to
/// multiple rows without repeating the header per row.
/// </summary>
internal static class ManifestWriter
{
    /// <summary>Writes the manifest CSV to disk as UTF-8 without a BOM, creating the parent directory when needed.</summary>
    /// <param name="results">The per-input results to serialize.</param>
    /// <param name="path">Absolute destination path.</param>
    public static void Write(IReadOnlyList<FileResult> results, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, ToCsv(results), new UTF8Encoding(false));
    }

    /// <summary>
    /// Reads a manifest CSV back into <see cref="FileResult"/> objects, collapsing the
    /// one-row-per-new-file layout back into one <see cref="FileResult"/> per original
    /// input file.
    /// </summary>
    /// <param name="path">Absolute path to a manifest previously produced by <see cref="Write"/>.</param>
    /// <returns>One <see cref="FileResult"/> per unique <c>originalPath</c> value, with its new files aggregated.</returns>
    public static IReadOnlyList<FileResult> Read(string path)
    {
        using var parser = new CsvFieldReader(path);
        var header = parser.ReadFields() ?? Array.Empty<string>();
        var indexes = header.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        var rows = new List<(string OriginalPath, string KeptPath, bool GitMove, string Status, string Reason, string Note, string NewFilePath, string Type)>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null || fields.Length == 0)
            {
                continue;
            }

            rows.Add((
                Get(fields, indexes, "originalPath"),
                Get(fields, indexes, "keptPath"),
                bool.TryParse(Get(fields, indexes, "gitMove"), out var gitMove) && gitMove,
                Get(fields, indexes, "status"),
                Get(fields, indexes, "reason"),
                Get(fields, indexes, "note"),
                Get(fields, indexes, "newFilePath"),
                Get(fields, indexes, "type")));
        }

        return rows
            .GroupBy(r => r.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new FileResult(
                    first.OriginalPath,
                    first.KeptPath,
                    first.GitMove,
                    first.Status,
                    string.IsNullOrEmpty(first.Reason) ? null : first.Reason,
                    string.IsNullOrEmpty(first.Note) ? null : first.Note,
                    g.Where(r => !string.IsNullOrEmpty(r.NewFilePath)).Select(r => new NewFileResult(r.NewFilePath, r.Type)).ToArray());
            })
            .ToArray();
    }

    private static string Get(string[] fields, Dictionary<string, int> indexes, string name)
    {
        return indexes.TryGetValue(name, out var index) && index < fields.Length ? fields[index] : string.Empty;
    }

    /// <summary>
    /// Serializes the results to the manifest CSV format. Rows without any new files
    /// (skips and failures) still emit one row so their status is preserved; splits
    /// emit one row per new file so the content phase can read the exact target paths.
    /// </summary>
    public static string ToCsv(IReadOnlyList<FileResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("originalPath,keptPath,gitMove,status,reason,note,newFilePath,type");
        foreach (var result in results)
        {
            if (result.NewFiles.Count == 0)
            {
                AppendRow(builder, result, null);
                continue;
            }

            foreach (var file in result.NewFiles)
            {
                AppendRow(builder, result, file);
            }
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, FileResult result, NewFileResult? file)
    {
        builder.Append(Csv(result.OriginalPath)).Append(',')
            .Append(Csv(result.KeptPath)).Append(',')
            .Append(result.GitMove ? "true" : "false").Append(',')
            .Append(Csv(result.Status)).Append(',')
            .Append(Csv(result.Reason ?? string.Empty)).Append(',')
            .Append(Csv(result.Note ?? string.Empty)).Append(',')
            .Append(Csv(file?.Path ?? string.Empty)).Append(',')
            .Append(Csv(file?.Type ?? string.Empty)).AppendLine();
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
