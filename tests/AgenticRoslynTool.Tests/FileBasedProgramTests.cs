using System.IO;
using Xunit;

namespace AgenticRoslynTool.Tests;

// A .NET 10 file-based program starts with #: or #! lines. The compiler accepts them only
// with the FileBasedProgram feature. Version 0.2.0 parsed without it, so every such file
// was marked failed and the run exited 1. Real repositories hit this: aspire, Humanizer,
// and Polly all ship file-based scripts. Decision 33.
public sealed class FileBasedProgramTests
{
    [Fact]
    public void PackageDirective_WithTopLevelStatements_IsSkippedNotFailed()
    {
        // The shape of Polly's cake.cs: #: lines, then a script.
        var result = PlanOne(
            "#:sdk Cake.Sdk\n" +
            "#:package Newtonsoft.Json@13.0.3\n" +
            "#:property ProjectType=Test\n" +
            "using System;\n" +
            "\n" +
            "Console.WriteLine(new Helper().Name);\n" +
            "\n" +
            "internal class Helper { public string Name => \"x\"; }\n" +
            "internal class Other { }\n");

        Assert.Equal("skipped", result.Status);
        Assert.Equal(SourceParsing.FileBasedProgramReason, result.Reason);
    }

    [Fact]
    public void ShebangOnly_IsSkippedNotFailed()
    {
        // The shape of aspire's BumpVersion.cs: a shebang and no #: lines.
        var result = PlanOne(
            "#!/usr/bin/env dotnet\n" +
            "using System;\n" +
            "\n" +
            "Console.WriteLine(1);\n" +
            "\n" +
            "internal class First { }\n" +
            "internal class Second { }\n");

        Assert.Equal("skipped", result.Status);
        Assert.Equal(SourceParsing.FileBasedProgramReason, result.Reason);
    }

    [Fact]
    public void FileBasedProgram_WithoutTopLevelStatements_IsNotSplit()
    {
        // A file-based program compiles alone. A type moved to a new file would drop out of
        // the program, so the top-level statement check cannot be what refuses this file.
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("Tool.cs",
            "#!/usr/bin/env dotnet\n" +
            "#:package Spectre.Console@0.49.1\n" +
            "namespace Scripts;\n" +
            "\n" +
            "internal static class Tool\n" +
            "{\n" +
            "    private static void Main() => System.Console.WriteLine(new Helper());\n" +
            "}\n" +
            "\n" +
            "internal sealed class Helper { }\n");
        var list = ws.WriteInputList(path);
        var manifest = Path.Combine(ws.Root, "m.csv");

        var plan = Assert.Single(new FileSplitter(new Options(list, manifest, ws.Root, Phase.Plan, null)).Run());
        Assert.Equal("skipped", plan.Status);
        Assert.Equal(SourceParsing.FileBasedProgramReason, plan.Reason);

        ws.RunPlanThenContent(list, manifest);
        Assert.False(File.Exists(Path.Combine(ws.Root, "Helper.cs")));
    }

    [Fact]
    public void OrdinaryFile_StillSplits()
    {
        // The feature flag must not change how a normal file is planned.
        var result = PlanOne(
            "namespace Sample;\n" +
            "\n" +
            "internal class OrdinaryFile { }\n" +
            "internal class Moved { }\n");

        Assert.Equal("split", result.Status);
    }

    private static FileResult PlanOne(string source)
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteFile("OrdinaryFile.cs", source);
        var options = new Options(ws.WriteInputList(path), Path.Combine(ws.Root, "m.csv"), ws.Root, Phase.Plan, null);
        return Assert.Single(new FileSplitter(options).Run());
    }
}
