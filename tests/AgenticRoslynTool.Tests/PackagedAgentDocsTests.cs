using System.Text.RegularExpressions;
using Xunit;

namespace AgenticRoslynTool.Tests;

// The package ships two agent documents: AGENTS.md at the root, which NuGet MCP returns,
// and skills/split-types/SKILL.md, which skill tools find. Both carry the workflow, so both
// can drift from the command line. These tests fail the build when one does. Decision 28.
public sealed partial class PackagedAgentDocsTests
{
    private static readonly string PackageDir = Path.Combine(RepositoryPaths.Root(), "package");

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("skills/split-types/SKILL.md")]
    public void PackagedDocs_RouteEveryToolBugReason(string relativePath)
    {
        // An agent told to "fix the cause" of a tool bug edits good code or loops. The docs
        // route rows by these two prefixes, so a reworded prefix in the code must fail here.
        var text = File.ReadAllText(Path.Combine(PackageDir, relativePath));

        Assert.Contains("`" + FileSplitter.SyntaxErrorsPrefix.TrimEnd(':', ' ') + "`", text, StringComparison.Ordinal);
        Assert.Contains("`" + FileSplitter.OutputCheckFailedPrefix.TrimEnd(':', ' ') + "`", text, StringComparison.Ordinal);
        Assert.Contains("Never edit a source file to get past the tool", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySkill_FollowsTheAgentSkillsSpecification()
    {
        // Limits from https://agentskills.io/specification. A tool that validates skills
        // skips one that breaks them, and dotnet-inspect lists only valid documents.
        var skills = Directory.GetFiles(Path.Combine(PackageDir, "skills"), "SKILL.md", SearchOption.AllDirectories);
        Assert.NotEmpty(skills);

        foreach (var skill in skills)
        {
            var fields = ReadFrontmatter(skill);
            var name = fields.GetValueOrDefault("name", string.Empty);
            Assert.Matches(SkillName(), name);
            Assert.True(name.Length <= 64, $"{skill}: name is over 64 characters");
            Assert.Equal(Path.GetFileName(Path.GetDirectoryName(skill)), name);

            var description = fields.GetValueOrDefault("description", string.Empty);
            Assert.InRange(description.Length, 1, 1024);
            Assert.True(fields.GetValueOrDefault("compatibility", string.Empty).Length <= 500, $"{skill}: compatibility is over 500 characters");
        }
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("skills/split-types/SKILL.md")]
    public void EveryDocumentedCommand_ParsesUnderTheCurrentOptions(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(PackageDir, relativePath));
        var commands = DocumentedCommand().Matches(text).Select(m => m.Groups["args"].Value).ToArray();

        // A rewrite that drops every command would otherwise pass with nothing checked.
        Assert.NotEmpty(commands);
        foreach (var command in commands)
        {
            // Placeholders such as <list file> stand for one value each.
            var args = Placeholder().Replace(command, "value").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var exception = Record.Exception(() => Options.Parse(args));
            Assert.True(exception is null, $"{relativePath}: `split-types {command}` no longer parses: {exception?.Message}");
        }
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("skills/split-types/SKILL.md")]
    public void EveryDocumentedFlagFragment_ParsesUnderTheCurrentOptions(string relativePath)
    {
        // Prose names flags on their own too, such as `--phase renames` or
        // `--collision-naming type-first`. A renamed flag or value must fail here as well.
        var text = File.ReadAllText(Path.Combine(PackageDir, relativePath));
        var fragments = FlagFragment().Matches(text).Select(m => m.Value.Trim('`')).Distinct().ToArray();
        Assert.Contains(fragments, f => f.StartsWith("--collision-naming ", StringComparison.Ordinal));

        foreach (var fragment in fragments)
        {
            var tokens = Placeholder().Replace(fragment, "value").Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // A flag named alone in prose, such as `--report` or `--file-naming`, only has to
            // be a flag the parser knows. Its value is not in the text, so it is not checked.
            if (tokens.Length == 1)
            {
                var unknown = Record.Exception(() => Options.Parse(["--input", "value", .. tokens])) is ArgumentException { Message: var bare }
                    && bare.StartsWith("Unknown argument:", StringComparison.Ordinal);
                Assert.False(unknown, $"{relativePath}: `{fragment}` is not a flag the parser knows");
                continue;
            }

            var exception = Record.Exception(() => Options.Parse(["--input", "value", .. tokens]));

            // --report and --phase plan are refused together by design; that is not drift.
            var refusedByDesign = exception is ArgumentException { Message: var why } && why.StartsWith("--report applies to", StringComparison.Ordinal);
            Assert.True(exception is null || refusedByDesign, $"{relativePath}: `{fragment}` no longer parses: {exception?.Message}");
        }
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("skills/split-types/SKILL.md")]
    public void TheDoneRule_QuotesTheToolsOwnReasons(string relativePath)
    {
        // The done rule is an allow list of two skip reasons. Reword either in the code and
        // the packaged docs stop matching what the tool prints. Line wrapping is not drift,
        // so runs of whitespace are folded to one space first.
        var text = string.Join(' ', File.ReadAllText(Path.Combine(PackageDir, relativePath)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(FileSplitter.NotPlannedReason, text, StringComparison.Ordinal);
        Assert.StartsWith("already applied", FileSplitter.AlreadyAppliedReason, StringComparison.Ordinal);
        Assert.Contains("starts with `already applied`", text, StringComparison.Ordinal);

        // The other two conditions SkillDoneRule checks. A third allowed reason would also
        // need SkillDoneRule changed, which this pins only through the two quoted ones.
        Assert.Contains("`phase` is `content`", text, StringComparison.Ordinal);
        Assert.Contains("`summary.failed` is", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("skills/split-types/SKILL.md")]
    public void TheFileLocalRule_QuotesTheNotePrefixTheToolWrites(string relativePath)
    {
        // Both documents tell an agent to match on how the note starts. Reword the note in
        // the code and they stop matching, which this fails on. FileLocalSplitTests pins
        // that a real row's note starts with the same constant.
        var text = string.Join(' ', File.ReadAllText(Path.Combine(PackageDir, relativePath)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("`note` starts with `" + FileLocalTypes.StayNotePrefix + "`", text, StringComparison.Ordinal);
        Assert.Contains("`reason` and `note`", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentsMd_PointsAtTheSkillItShipsWith()
    {
        Assert.Contains("skills/split-types/SKILL.md", File.ReadAllText(Path.Combine(PackageDir, "AGENTS.md")), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ReadFrontmatter(string path)
    {
        var lines = File.ReadAllLines(path);
        Assert.Equal("---", lines[0]);
        var end = Array.IndexOf(lines, "---", 1);
        Assert.True(end > 0, $"{path}: frontmatter is not closed");

        // Enough YAML for this file's shape: "key: value", or "key: >-" followed by indented
        // lines folded with spaces. A nested map, such as metadata, is skipped.
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < end; i++)
        {
            var match = TopLevelKey().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value.Trim();
            if (value is ">-" or ">" or "|" or "|-" or "")
            {
                var folded = new List<string>();
                while (i + 1 < end && lines[i + 1].StartsWith("  ", StringComparison.Ordinal))
                {
                    folded.Add(lines[++i].Trim());
                }

                value = string.Join(' ', folded);
            }

            fields[match.Groups["key"].Value] = value;
        }

        return fields;
    }


    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SkillName();

    [GeneratedRegex("^(?<key>[a-z][a-z-]*):(?<value>.*)$")]
    private static partial Regex TopLevelKey();

    [GeneratedRegex("`(?:<tool>|agentic-roslyn-tool) split-types (?<args>[^`]+)`")]
    private static partial Regex DocumentedCommand();

    [GeneratedRegex("`--[a-z][a-z-]*(?: [^`-][^`]*)?`")]
    private static partial Regex FlagFragment();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Placeholder();
}
