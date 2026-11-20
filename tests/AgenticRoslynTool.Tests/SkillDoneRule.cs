using System.Text.Json;

namespace AgenticRoslynTool.Tests;

// The packaged skill's test for a finished split, step 5 of package/skills/split-types/SKILL.md,
// written as code so tests can hold the tool to it. Decision 27.
internal static class SkillDoneRule
{
    public static bool Holds(JsonElement report) =>
        report.GetProperty("phase").GetString() == "content"
        && report.GetProperty("summary").GetProperty("failed").GetInt32() == 0
        && report.GetProperty("files").EnumerateArray()
            .Where(row => row.GetProperty("status").GetString() == "skipped")
            .Select(row => row.GetProperty("reason").GetString() ?? string.Empty)
            .All(reason => reason == FileSplitter.NotPlannedReason || reason.StartsWith("already applied", StringComparison.Ordinal));
}
