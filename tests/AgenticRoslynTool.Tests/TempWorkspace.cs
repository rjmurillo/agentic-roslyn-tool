using System;
using System.Collections.Generic;
using System.IO;

namespace AgenticRoslynTool.Tests;

// Per-test scratch directory that deletes itself on Dispose. Kept internal because
// several test classes share it; nothing outside this assembly needs it.
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "art-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string WriteFile(string relativePath, string contents)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    // Writes an --input list file that names one path per line.
    public string WriteInputList(params string[] filePaths)
    {
        var listPath = Path.Combine(Root, "inputs.txt");
        File.WriteAllLines(listPath, filePaths);
        return listPath;
    }

    // Runs the two-phase flow the CLI uses: Plan writes the manifest, Content consumes it.
    // Renames are not exercised here so the primary type must match the file base name.
    public IReadOnlyList<FileResult> RunPlanThenContent(string listPath, string manifestPath)
    {
        var planOptions = new Options(listPath, manifestPath, Root, Phase.Plan, null);
        var planResults = new FileSplitter(planOptions).Run();
        ManifestWriter.Write(planResults, manifestPath);

        var contentOptions = new Options(listPath, manifestPath, Root, Phase.Content, null);
        return new FileSplitter(contentOptions).Run();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup. A leaked temp directory is not worth failing a test over.
        }
    }
}
