using System.Diagnostics;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Shared arrangement for the content hash tests: one scratch directory and one manifest
// path per test, and the phase runners every one of those tests drives. xUnit builds a new
// instance for each test, so nothing here is shared between tests.
public abstract class ContentHashTestBase : IDisposable
{
    private protected const string TwoTypes = "namespace N;\n\npublic class Foo\n{\n    public int M() => 1;\n}\n\npublic class Bar\n{\n}\n";

    // Kept type does not match the file name, so the plan asks for a rename to Order2.cs.
    private protected const string Mismatched = "namespace N;\n\npublic class Order2 { }\n\npublic class Line { }\n";

    private protected TempWorkspace Workspace { get; } = new();

    private protected string ManifestPath => Path.Combine(Workspace.Root, "manifest.csv");

    public void Dispose()
    {
        Workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    private protected IReadOnlyList<FileResult> Plan(params string[] inputs) =>
        RunAndWriteManifest(Phase.Plan, inputs).ManifestRows;

    private protected RunOutcome Content(params string[] inputs) =>
        new FileSplitter(new Options(Workspace.WriteInputList(inputs), ManifestPath, Workspace.Root, Phase.Content, null)).Run();

    // Runs one phase and writes its manifest rows back to the manifest path, which is what
    // Program does after every phase. Tests that chain phases need that write to be real.
    private protected RunOutcome RunAndWriteManifest(Phase phase, params string[] inputs)
    {
        var outcome = new FileSplitter(new Options(Workspace.WriteInputList(inputs), ManifestPath, Workspace.Root, phase, null)).Run();
        ManifestWriter.Write(outcome.ManifestRows, ManifestPath);
        return outcome;
    }

    // The renames phase runs git mv, which needs a repository and a tracked file. Nothing is
    // committed, so no author identity is required on the machine running the test.
    private protected void InitRepository(string trackedFile)
    {
        Git("init", "--quiet");
        Git("add", "--", trackedFile);
    }

    private void Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Workspace.Root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start git");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }
}
