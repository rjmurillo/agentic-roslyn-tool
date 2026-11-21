using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgenticRoslynTool.Tests;

// Shared arrangement for the file-local type tests: one scratch directory and one manifest
// per test, and the phase runners each test drives. xUnit builds a new instance for each
// test, so nothing here is shared between tests.
public abstract class FileLocalTestBase : IDisposable
{
    // Alpha uses the file-local helper, so it can never move. Sibling uses nothing.
    private protected const string AlphaWithHelper =
        "namespace Sample;\n\ninternal class Alpha\n{\n    public static string HelperName() => typeof(AlphaHelper).FullName;\n}\n\n"
        + "file class AlphaHelper\n{\n}\n\ninternal class Sibling\n{\n    public int Value => 1;\n}\n";

    private protected TempWorkspace Workspace { get; } = new();

    private protected string ManifestPath => Path.Combine(Workspace.Root, "manifest.csv");

    // The --file-naming value every phase of a test runs under.
    private protected FileNamingConvention Naming { get; set; } = FileNamingConvention.StyleCop;

    public void Dispose()
    {
        Workspace.Dispose();
        GC.SuppressFinalize(this);
    }

    private protected string Write(string name, string text) => Workspace.WriteFile(name, text);

    private protected string PathOf(string name) => Path.Combine(Workspace.Root, name);

    private protected string[] CsFiles() => FileNamingRuns.CsFiles(Workspace);

    private protected CSharpCompilation Compile(params string[] symbols) => FileLocalCompiler.CompileDirectory(Workspace.Root, symbols);

    // Runs one phase the way Program does: the plan phase writes the manifest, and no later
    // phase writes it at all.
    private protected FileResult[] Run(Phase phase, params string[] inputs)
    {
        return FileNamingRuns.RunPhase(Workspace, Workspace.WriteInputList(inputs), ManifestPath, phase, Naming);
    }

    // Plans one input the tool must refuse, then runs the content phase over the plan.
    // Checks the reason and the note, and that neither phase changed a file.
    private protected void AssertRefused(string path, string reason, string? note)
    {
        var text = File.ReadAllText(path);
        var files = CsFiles();

        var planned = Assert.Single(Run(Phase.Plan, path));
        var applied = Assert.Single(Run(Phase.Content, path));

        Assert.Equal(("skipped", reason, note), (planned.Status, planned.Reason, planned.Note));
        Assert.Equal(("skipped", FileSplitter.NotPlannedReason), (applied.Status, applied.Reason));
        Assert.Empty(planned.NewFiles);
        Assert.Equal(text, File.ReadAllText(path));
        Assert.Equal(files, CsFiles());
    }

    // Compiles the input as written, then compiles what a split would write if nothing
    // refused it. The first must be clean. The caller asserts on the second.
    private protected CSharpCompilation CompileUncheckedSplit(string path, params (string Path, string Text)[] others)
    {
        var text = File.ReadAllText(path);
        Assert.Empty(FileLocalCompiler.Diagnostics(FileLocalCompiler.Compile([(path, text), .. others])));
        return FileLocalCompiler.Compile([.. FileLocalCompiler.SplitUnchecked(path, text), .. others]);
    }
}
