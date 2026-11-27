using Xunit;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Runs plan then content over one source file named <c>Alpha.cs</c> and reads what it wrote.
/// Shared by the nullable context test classes.
/// </summary>
internal static class NullableSplitFixture
{
    /// <summary>The opening every sample shares: a file scoped namespace and a blank line.</summary>
    internal const string Namespace = "namespace Sample.Ns;\n\n";

    /// <summary>The first type in most samples. It stays in <c>Alpha.cs</c>.</summary>
    internal const string Alpha = "public class Alpha { }\n\n";

    /// <summary>Splits <paramref name="source"/> and returns its one result row.</summary>
    internal static FileResult Run(TempWorkspace ws, string source, string? requiredHeader = null)
    {
        var alphaPath = ws.WriteFile("Alpha.cs", source);
        return Assert.Single(ws.RunPlanThenContent(ws.WriteInputList(alphaPath), Path.Combine(ws.Root, "m.csv"), requiredHeader));
    }

    /// <summary>Splits <paramref name="source"/>, requires a split row, and checks every type's context with the compiler.</summary>
    internal static void SplitPreserved(TempWorkspace ws, string source, string? requiredHeader = null)
    {
        var row = Run(ws, source, requiredHeader);
        Assert.True(row.Status == "split", $"{row.Status}: {row.Reason}");
        NullableContextOracle.AssertPreserved(source, ws.Root);
    }

    /// <summary>Reads one file the run wrote.</summary>
    internal static string Read(TempWorkspace ws, string name)
    {
        return File.ReadAllText(Path.Combine(ws.Root, name));
    }

    /// <summary>Counts whole lines equal to <paramref name="line"/>, under either newline.</summary>
    internal static int CountLine(string text, string line)
    {
        return text.Replace("\r\n", "\n").Split('\n').Count(l => l == line);
    }
}
