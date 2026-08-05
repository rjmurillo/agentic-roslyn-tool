using System.IO;
using System.Text.Json;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The JSON report is the contract an agent parses instead of the CSV. Its status
// vocabulary and field names must match the manifest, and its counts must be usable
// without re-tallying the rows.
public sealed class RunReportTests
{
    private const string TwoTypes = "namespace N;\n\npublic class Foo\n{\n}\n\npublic class Bar\n{\n}\n";

    [Fact]
    public void Parse_DefaultsToCsvOutput()
    {
        Assert.False(Options.Parse(["--input", "list.txt"]).Json);
        Assert.True(Options.Parse(["--input", "list.txt", "--json"]).Json);
    }

    [Fact]
    public void Report_CountsEachStatusAndTheFilesCreated()
    {
        using var workspace = new TempWorkspace();
        var split = workspace.WriteFile("Foo.cs", TwoTypes);
        var excluded = workspace.WriteFile(Path.Combine("generated", "Gen.cs"), TwoTypes);

        var listPath = workspace.WriteInputList(split, excluded);
        var options = new Options(listPath, Path.Combine(workspace.Root, "m.csv"), workspace.Root, Phase.Plan, null, ["generated/"]);
        var results = new FileSplitter(options).Run().ReportRows;

        var report = RunReport.Create(results, Phase.Plan, options.ManifestPath);

        Assert.Equal(2, report.Summary.Total);
        Assert.Equal(1, report.Summary.Split);
        Assert.Equal(1, report.Summary.Skipped);
        Assert.Equal(0, report.Summary.Failed);
        Assert.Equal(1, report.Summary.NewFiles);
        Assert.Equal("plan", report.Phase);
    }

    [Fact]
    public void Json_UsesTheSameFieldNamesAndStatusVocabularyAsTheManifest()
    {
        using var workspace = new TempWorkspace();
        var source = workspace.WriteFile("Foo.cs", TwoTypes);
        var listPath = workspace.WriteInputList(source);
        var options = new Options(listPath, Path.Combine(workspace.Root, "m.csv"), workspace.Root, Phase.Plan, null);
        var results = new FileSplitter(options).Run().ReportRows;

        using var document = JsonDocument.Parse(RunReport.Create(results, Phase.Plan, options.ManifestPath).ToJson());
        var root = document.RootElement;

        Assert.Equal("plan", root.GetProperty("phase").GetString());
        Assert.Equal(options.ManifestPath, root.GetProperty("manifest").GetString());

        var file = Assert.Single(root.GetProperty("files").EnumerateArray());
        Assert.Equal("split", file.GetProperty("status").GetString());
        Assert.Equal(source, file.GetProperty("originalPath").GetString());
        Assert.True(file.GetProperty("gitMove").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.Equal(JsonValueKind.Null, file.GetProperty("reason").ValueKind);
        Assert.Single(file.GetProperty("newFiles").EnumerateArray());

        // The nested entry carries both CSV columns. An earlier version emitted a bare
        // path array, which dropped the type name and made JSON a weaker contract than CSV.
        var newFile = file.GetProperty("newFiles")[0];
        Assert.EndsWith("Bar.cs", newFile.GetProperty("newFilePath").GetString());
        Assert.Equal("Bar", newFile.GetProperty("type").GetString());
    }

    [Fact]
    public void SummaryLine_NamesThePhaseAndEveryCount()
    {
        var report = new RunReport("content", "m.csv", new RunSummary(3, 1, 1, 1, 2), []);

        Assert.Equal("content: 3 input(s), 1 split, 1 skipped, 1 failed, 2 new file(s).", report.ToSummaryLine());
    }
}
