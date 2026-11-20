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
    /// <param name="collisionNaming">
    /// The collision naming the run used. Required, never defaulted, because a later phase
    /// takes it from the manifest instead of from its own command line.
    /// </param>
    public static void Write(IReadOnlyList<FileResult> results, string path, CollisionNaming collisionNaming)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, ToCsv(results, collisionNaming), new UTF8Encoding(false));
    }

    /// <summary>
    /// Reads a manifest CSV back into <see cref="FileResult"/> objects, collapsing the
    /// one-row-per-new-file layout back into one <see cref="FileResult"/> per original
    /// input file.
    /// </summary>
    /// <param name="path">Absolute path to a manifest previously produced by <see cref="Write"/>.</param>
    /// <param name="requireContentHash">
    /// True when the caller is about to act on the rows' content hashes. A manifest with no
    /// <c>contentHash</c> column then fails the read, before any file is touched.
    /// </param>
    /// <returns>One <see cref="FileResult"/> per unique <c>originalPath</c> value, with its new files aggregated.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="requireContentHash"/> is set and the manifest has no <c>contentHash</c> column.</exception>
    public static IReadOnlyList<FileResult> Read(string path, bool requireContentHash = false)
    {
        using var parser = new CsvFieldReader(path);
        var header = parser.ReadFields() ?? Array.Empty<string>();
        var indexes = header.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        if (requireContentHash && !indexes.ContainsKey("contentHash"))
        {
            // A missing column is a property of the whole file, so it is one error for the
            // run and not one skip per row. A run that skipped every row would exit 0 and
            // look like a run with nothing to do.
            throw new InvalidOperationException($"Plan manifest has no contentHash column, so it cannot prove the inputs are unchanged. An older version wrote it, or it is not a plan manifest. Re-run --phase plan to rebuild it: {path}");
        }

        var rows = new List<(string OriginalPath, string KeptPath, bool GitMove, string Status, string Reason, string Note, string NewFilePath, string Type, string ContentHash)>();
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
                Get(fields, indexes, "type"),
                Get(fields, indexes, "contentHash")));
        }

        // The CSV repeats one logical row once per new file, so grouping reassembles them.
        // PathComparison.Comparer, not OrdinalIgnoreCase: on a case-sensitive filesystem
        // Foo.cs and foo.cs are two files with two plans, and merging them would apply one
        // file's plan to the other.
        return rows
            .GroupBy(r => r.OriginalPath, PathComparison.Comparer)
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
                    g.Where(r => !string.IsNullOrEmpty(r.NewFilePath)).Select(r => new NewFileResult(r.NewFilePath, r.Type)).ToArray())
                {
                    // Kept as written, valid or not. Judging the value is the job of the
                    // phase that compares it, and a malformed one has to reach that phase
                    // to be refused.
                    ContentHash = string.IsNullOrEmpty(first.ContentHash) ? null : first.ContentHash,
                };
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
    /// New columns are appended, never inserted, so a reader that addresses columns by
    /// position keeps working.
    /// </summary>
    /// <param name="results">The per-input results to serialize.</param>
    /// <param name="collisionNaming">The run's collision naming, repeated on every row. See <see cref="ManifestCollisionNaming"/>.</param>
    public static string ToCsv(IReadOnlyList<FileResult> results, CollisionNaming collisionNaming)
    {
        var builder = new StringBuilder();
        builder.AppendLine("originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash," + ManifestCollisionNaming.Column);
        var naming = ManifestCollisionNaming.Format(collisionNaming);
        foreach (var result in results)
        {
            if (result.NewFiles.Count == 0)
            {
                AppendRow(builder, result, null, naming);
                continue;
            }

            foreach (var file in result.NewFiles)
            {
                AppendRow(builder, result, file, naming);
            }
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, FileResult result, NewFileResult? file, string collisionNaming)
    {
        builder.Append(Csv(result.OriginalPath)).Append(',')
            .Append(Csv(result.KeptPath)).Append(',')
            .Append(result.GitMove ? "true" : "false").Append(',')
            .Append(Csv(result.Status)).Append(',')
            .Append(Csv(result.Reason ?? string.Empty)).Append(',')
            .Append(Csv(result.Note ?? string.Empty)).Append(',')
            .Append(Csv(file?.Path ?? string.Empty)).Append(',')
            .Append(Csv(file?.Type ?? string.Empty)).Append(',')
            .Append(Csv(result.ContentHash ?? string.Empty)).Append(',')
            .Append(Csv(collisionNaming)).AppendLine();
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
