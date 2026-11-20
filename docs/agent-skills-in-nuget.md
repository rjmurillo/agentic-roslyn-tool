# Agent Skills in a .NET tool package

This page records what was tested on 2026-11-20 about shipping an Agent Skill inside
the `AgenticRoslynTool` NuGet package. It covers what works, what does not, and how to
check it again. Read it before you change `package/`, the csproj `Pack` items, or the
package checks in CI. Every claim below was run, not inferred, unless it says otherwise.

## The two Microsoft posts, and which one applies

Two posts from November 2026 talk about "skills" and NuGet. They describe different things.

| Post | What it is | Applies to this tool |
| --- | --- | --- |
| [Agent Skills for .NET is now released](https://devblogs.microsoft.com/agent-framework/agent-skills-for-net-is-now-released/) | A runtime library in Microsoft Agent Framework (`Microsoft.Agents.AI`, `AgentSkillsProvider`). An app you build loads skills and exposes `load_skill`, `read_skill_resource`, and `run_skill_script` to its own agent. | No. Coding agents such as Claude Code or Copilot run this tool from a shell. They are not Agent Framework apps. Only the `SKILL.md` format is shared. |
| [Delivering skills in NuGet packages](https://devblogs.microsoft.com/dotnet/delivering-skills-in-nuget-packages/) | A packaging convention. Put `skills/<name>/SKILL.md` at the package root. Restore puts it on disk, and tools such as `dotnet-inspect` list it. | Yes. A tool package is a NuGet package, and the files land on disk. See the limits below. |

The second post does not mention tool packages. A reader comment asks "Will this also work
for .NET tool packages?" and had no answer when it was read. The tests on this page are the
answer for this package.

## Where the skill lands

`dotnet pack` puts the file at `skills/split-types/SKILL.md` in the `.nupkg`. Each install
path extracts it. Tested with a locally packed `0.0.0-skilltest4` and a private
`NUGET_PACKAGES` folder:

| Install path | Where `SKILL.md` lands |
| --- | --- |
| `dotnet tool install --global` or `--tool-path <dir>` | `<tools>/.store/agenticroslyntool/<version>/agenticroslyntool/<version>/skills/split-types/SKILL.md` |
| Local tool manifest, `dotnet tool install` | `~/.nuget/packages/agenticroslyntool/<version>/skills/split-types/SKILL.md` |
| `dnx AgenticRoslynTool` | The same NuGet cache path as a local tool |

For a global tool, `<tools>` is `~/.dotnet/tools`. The package `AGENTS.md` sits in the same
folder as `skills/`.

## What finds the skill, and what cannot

| Discovery route | Result | Why |
| --- | --- | --- |
| `dnx dotnet-inspect@0.28.0 -- package AgenticRoslynTool -S Skills` | Lists `skills/split-types/SKILL.md` | Reads the package from the registry or a source. Tested with `dotnet-inspect` 0.28.0. |
| `dotnet-inspect package AgenticRoslynTool -S Skills --row 1 --print` | Prints the skill | Same route. Use it to load the skill into an agent's context. |
| `dotnet-inspect` 0.5.3 | Shows no skills section, for any package | Too old. Update it, or run the current version through `dnx`. |
| `dotnet-inspect project <csproj> -S Skills` | Never sees a tool's skill | It reads direct `PackageReference`s from `project.assets.json`. A tool is not one. |
| A `PackageReference` to `AgenticRoslynTool` | Restore fails with `NU1212` | NuGet refuses to mix a `DotnetTool` package with a project. So the project graph can never hold this package. |
| `dotnet-package-skills install` | Not tested | The post describes it, but no package by that name was found on nuget.org. It scans the project graph, so the `NU1212` limit would apply to it too. |
| NuGet MCP `get_package_context` | Returns `AGENTS.md`, not the skill | The post says NuGet MCP has not been updated for skills yet. See [Roslyn MCP vs CLI](roslyn-mcp-vs-cli.md) for what it returns today. |

So the skill reaches an agent three ways: a registry query through `dotnet-inspect`, a read
of the on-disk path above, or a copy that you commit into your repository's
`.agents/skills/split-types/`. A copy does not update itself. Re-copy it when you update the
tool.

## Why the package has two agent documents

`package/AGENTS.md` and `package/skills/split-types/SKILL.md` both describe the workflow.
That looks like duplication, and it is on purpose.

- NuGet MCP returns `AGENTS.md`. It does not read skills yet. Dropping the workflow from
  `AGENTS.md` would leave NuGet MCP users with a pointer and nothing else.
- Skill tools read `SKILL.md`. They do not read `AGENTS.md`.

Drift between the two is handled by `PackagedAgentDocsTests`. It fails the build when a
full `split-types` command line in either file stops parsing under the current options,
when a backticked flag fragment such as `--collision-naming type-first` stops parsing, when
a skill breaks the format rules below, or when `AGENTS.md` stops naming the skill. Each
check was seen to fail: injecting `--bogus` into a command, renaming the skill to
`Split_Types`, and changing `type-first` to `type-last` in a fragment. It checks that
commands still parse. It does not check that the two files say the same thing in prose.
See decision 27.

## Format rules the skill must follow

From the [Agent Skills specification](https://agentskills.io/specification), read on
2026-11-20:

- `SKILL.md` starts with YAML frontmatter, then Markdown.
- `name` is required. 1 to 64 characters, lowercase letters, digits, and single hyphens, no
  hyphen at either end. It must equal the parent folder name.
- `description` is required. 1 to 1024 characters. Say what the skill does and when to use
  it, with the words a user would type, such as `SA1402` and `dotnet format`.
- `license`, `compatibility` (up to 500 characters), `metadata`, and `allowed-tools` are
  optional.

## Packing it

The csproj line that ships the skill:

```xml
<None Include="..\..\package\skills\**\*" Pack="true" PackagePath="skills" />
```

The `PackagePath` value was found by trial, and two plausible values are wrong:

| `PackagePath` | Entry in the `.nupkg` |
| --- | --- |
| `skills\%(RecursiveDir)` | `skills/split-types/split-types/SKILL.md`, one level too deep |
| `skills\` | `skills//split-types/SKILL.md`, a double slash in the entry name |
| `skills` | `skills/split-types/SKILL.md`, correct |

NuGet appends the wildcard's folder by itself. `.github/scripts/verify-package-contents.sh`
checks the exact entry name in CI and in the release workflow, before anything is pushed.
It was run against all three packages above and rejected the two wrong ones.

## How to check it again

Run these from the repository root. They use a scratch NuGet folder so your real cache and
tools stay untouched. On Windows, use the same commands in PowerShell with `$env:` syntax.

```bash
scratch="$(mktemp -d)"
export NUGET_PACKAGES="$scratch/packages" DOTNET_CLI_HOME="$scratch/home" DOTNET_NOLOGO=1
dotnet pack src/AgenticRoslynTool -c Release -o "$scratch/feed" -p:Version=0.0.0-check
bash .github/scripts/verify-package-contents.sh "$scratch/feed"
dotnet tool install AgenticRoslynTool --version 0.0.0-check --add-source "$scratch/feed" --tool-path "$scratch/tools"
find "$scratch/tools" -name SKILL.md
dnx dotnet-inspect@0.28.0 --yes -- package AgenticRoslynTool --version 0.0.0-check --source "$scratch/feed" -S Skills
```

Expect the verify script to exit 0, `find` to print one path ending in
`skills/split-types/SKILL.md`, and `dotnet-inspect` to list that path. Without
`DOTNET_NOLOGO`, a fresh `DOTNET_CLI_HOME` prints the .NET first run banner once.

## Frontier and economical models

The NuGet post reports what its authors measured. With frontier models they "didn't find
much room to improve efficacy." With weaker models they "observed significant efficacy
underperformance and variability due to lower skill compliance." One skill set reaches
every model, so a skill that helps a small model but costs a frontier model more is "a bad
trade." Testing only with cheap models is called "an anti-pattern."

That points away from writing more prose for weaker models, and toward a command line that
is hard to misuse. These changes shipped with the skill, and each one is in the decision log:

- A run with no `--phase` plans and changes nothing. Decision 24.
- The plan records `--collision-naming`, and a later phase that contradicts it exits 3.
  Decision 25.
- The JSON report carries `nextArgs`, the exact arguments for the next phase. Decision 26.
- `--help` prints the three step workflow and says that exit 0 can mean nothing happened.

The skill itself is short and leans on `nextArgs`, so a model follows one literal command
at a time. No skill evaluation has been run. Nothing here shows the skill makes any model
better or cheaper. To measure it, give one task, such as "enable SA1402 in this repository",
to a frontier model and to an economical one, with and without the skill. Compare success,
turns, and tokens. Keep the skill only if the frontier run costs no more with it.

## Safety

The NuGet post warns that package Markdown read by an agent is a new attack surface, and
that the old rule still holds: only use packages from maintainers you trust. This skill has
no `scripts/` folder and asks for no tools. It tells an agent to run this package's own
command line and nothing else.
