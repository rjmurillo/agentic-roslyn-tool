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
        RunPhase(Phase.Plan, inputs);

    private protected IReadOnlyList<FileResult> Content(params string[] inputs) =>
        RunPhase(Phase.Content, inputs);

    // Runs one phase and does with its rows what Program does: the plan phase writes them
    // to the manifest, and no later phase writes the manifest at all. Tests that chain
    // phases need the manifest on disk to be the one a real run would leave.
    private protected IReadOnlyList<FileResult> RunPhase(Phase phase, params string[] inputs)
    {
        var rows = new FileSplitter(new Options(Workspace.WriteInputList(inputs), ManifestPath, Workspace.Root, phase, null)).Run(out var collisionNaming);
        if (phase == Phase.Plan)
        {
            ManifestWriter.Write(rows, ManifestPath, collisionNaming);
        }

        return rows;
    }

    // See GitFixture: a repository and a tracked file are enough for git mv.
    private protected void InitRepository(string trackedFile) => GitFixture.InitAndTrack(Workspace.Root, trackedFile);
}
