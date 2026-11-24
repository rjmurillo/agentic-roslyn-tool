using System;
using System.IO;

namespace AgenticRoslynTool.Tests;

// Finds the repository root from the test output directory, for tests that read files
// checked into the repository.
internal static class RepositoryPaths
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgenticRoslynTool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("AgenticRoslynTool.slnx not found above the test output directory");
    }
}
