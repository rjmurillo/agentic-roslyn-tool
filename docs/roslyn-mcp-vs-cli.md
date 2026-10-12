# Roslyn MCP servers vs a single-purpose Roslyn CLI: when an AI agent needs which

Use a Roslyn MCP server when an agent needs to ask questions about C# code as it works:
find references, navigate symbols, read diagnostics, make targeted refactorings. Use a
single-purpose CLI such as agentic-roslyn-tool when the job is one mechanical change across
a whole repository that needs a reviewable plan, verification, and a clear pass or fail exit
code. Many teams want both.

agentic-roslyn-tool is not an MCP server. It is a .NET tool with one verb, `split-types`,
that an agent calls through the shell.

## What a Roslyn MCP server gives an agent

An [MCP](https://modelcontextprotocol.io) server runs beside the agent and exposes tools the
agent can call in the middle of a conversation. The Roslyn servers below open a solution or
project and answer from the [Roslyn](https://learn.microsoft.com/dotnet/csharp/roslyn-sdk/)
compiler's view of it, so they can resolve types, find every reference to a symbol, and
apply refactorings that update call sites.

Most of them load a solution or project before they answer. Their READMEs say so: SharpToolsMCP's
entry point is a `SharpTool_LoadSolution` tool, Roslyn-Backed-MCP says it "uses Roslyn and
`MSBuildWorkspace`", and RoslynMcpServer lists "A C# solution (`.sln` or `.slnx`) or project
(`.csproj`) to work with" as a prerequisite.

Each description below is quoted from the project's GitHub repository or, where noted, its
NuGet package. Tool counts are what each project reports about itself. A dash means no
repository or no NuGet package was found for that row.

As of 2026-10-11:

| Server | Its own description | GitHub stars | NuGet downloads |
|---|---|---|---|
| [CWM.RoslynNavigator](https://github.com/codewithmukesh/dotnet-claude-kit) | "A Model Context Protocol (MCP) server that gives AI coding assistants semantic understanding of .NET codebases via Roslyn." (NuGet) | 758, for the whole dotnet-claude-kit repository | 9,723 (`CWM.RoslynNavigator`) |
| [glider](https://glidermcp.com) | "Compiler-backed C#/.NET code intelligence for coding agents." (NuGet) | - | 140,532 (`glider`) |
| [mcp-ai-server-visual-studio](https://github.com/LadislavSopko/mcp-ai-server-visual-studio) | "MCP AI Server - Roslyn-powered MCP server for Visual Studio. 20 tools for AI assistants." | 52 | - (Visual Studio Marketplace) |
| [Roslyn-Backed-MCP](https://github.com/darylmcd/Roslyn-Backed-MCP) | "Roslyn-backed MCP server providing semantic C# analysis for AI coding agents" (NuGet) | 1 | 6,793 (`Darylmcd.RoslynMcp`) |
| [roslyn-codelens-mcp](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp) | "Roslyn-based MCP server giving AI agents deep semantic understanding of .NET/C# codebases", with "67 tools for navigation, call graphs, diagnostics & code fixes, safe refactoring, code-quality auditing, test intelligence, DI graphs, and IL/external-assembly inspection." | 60 | 18,240 (`RoslynCodeLens.Mcp`) |
| [RoslynMcp](https://github.com/MadQ/RoslynMcp) | "Roslyn-powered MCP server for C# code intelligence" | 19 | 349 (`MadQ.RoslynMcp`, prerelease) |
| [RoslynMcpServer](https://github.com/JoshuaRamirez/RoslynMcpServer) | "Model Context Protocol server for Roslyn-powered C# refactoring operations" | 31 | 12,267 (`RoslynMcp.Server`) |
| [SharpToolsMCP](https://github.com/kooshi/SharpToolsMCP) | "A suite of MCP tools for AIs to analyze and modify C# solutions with high signal, Roslyn powered context." | 225 | - |
| [sharplens-mcp](https://github.com/pzalutski-pixel/sharplens-mcp) | "Roslyn-powered MCP server for AI-driven C# semantic analysis, refactoring, and code generation" | 36 | 6,454 (`SharpLensMcp`) |

Stars and downloads are popularity signals, not quality signals. NuGet download counts
include CI restores.

## What a single-purpose CLI gives an agent

agentic-roslyn-tool does one job: it splits C# files that declare more than one top-level
type into one file per type, so a codebase can turn on StyleCop SA1402. It is shaped for an
agent caller. See [decision 16](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/decision-log.md#16-the-command-line-is-an-agent-interface-first)
for why.

- **One command.** No server process, no session, no tool list to choose from. The same
  command runs in a terminal, in an agent's shell tool, and in CI with no agent at all.
- **Standard input and output.** `--input` takes a directory, a `.cs` file, a CSV, a list
  file, or `-` for paths on standard input. Standard output carries one document or
  nothing. Progress and errors go to standard error.
- **A JSON report.** `--json` prints one object that includes summary counts. Its fields
  are listed in the [JSON report contract](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/behavior-contracts.md#json-report).
- **Distinct exit codes.** 0 means no file failed, 1 means at least one file failed, 2
  means the command line was wrong, and 3 means the run could not complete.
- **A dry run and a reviewable plan.** `--dry-run` writes a CSV manifest and changes no
  source file. That manifest can be read, diffed, and committed before anything moves.
  The renames phase recomputes from the current files, so re-run the plan if the tree
  changed since you reviewed it.
- **Checks before every write.** For each file, the outputs are verified in memory before
  any byte reaches disk. A file that fails a check is left untouched and recorded as
  `failed` with a reason.

It reads syntax only. It builds no semantic model and loads no MSBuild workspace, so it
runs on code that does not compile as a set. The cost is that it cannot answer any question
that needs symbols. See [decision 1](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/decision-log.md#1-roslyn-syntax-api-with-no-semantic-model).

A dry run over a directory, with the plan kept outside the repository:

```bash
agentic-roslyn-tool split-types --input src --dry-run --json --manifest /tmp/split-plan.csv
```

An agent or a script can branch on the exit code without parsing anything first:

```bash
plan="$(mktemp)"
agentic-roslyn-tool split-types --input src --dry-run --json --manifest "$plan" > report.json
case $? in
  0) jq '.summary' report.json ;;
  1) jq -r '.files[] | select(.status == "failed") | "\(.originalPath): \(.reason)"' report.json ;;
  2) echo "The command line was wrong. Fix the arguments." ;;
  3) echo "The run could not complete. Read the error: line on standard error." ;;
esac
```

On a sample with one file that has a syntax error, the `1)` branch prints this line. The
real output carries the full absolute path.

```text
/repo/src/Broken.cs: input has syntax errors: CS1513 } expected
```

## Decision table

| The job | Reach for | Why |
|---|---|---|
| Ask an exploratory question about the code | MCP server | The agent needs answers mid-conversation, and the next question depends on the last answer. |
| Rename one symbol and every reference to it | MCP server | Finding references needs a semantic model. |
| Resolve types across projects | MCP server | This CLI has no semantic model and does not load projects. |
| Split every multi-type file in a repository for SA1402 | CLI | One mechanical change, planned, checked per file, and reported with an exit code. |
| Gate a CI job | CLI | A command with exit codes needs no agent and no server process. |
| Produce a run a reviewer can check in a pull request | CLI | The plan is a file, and renames and content land as separate commits. |

## Can an MCP server split every file?

Some can. RoslynMcpServer's `move_type_to_file` tool accepts `allFiles: true`, which, in its
README's words, "walks every C# file and extracts every eligible top-level type into
`{directory}/{TypeName}.cs`". It works on a loaded solution and updates references.
sharplens-mcp lists a `move_type_to_file` tool that will "Compute the contents to move a type
into its own file (generation-only)". Any agent can also loop a per-file tool over many files.

Previews exist on the server side too. RoslynMcpServer's refactoring tools accept a
`preview` parameter, and Roslyn-Backed-MCP describes a preview then apply flow so agents
"can dry-run multi-file edits before mutating the workspace".

So the choice is not about whether a split is possible. It is about what comes with it. The
CLI builds a few things in: a plan saved as a file you can commit and diff, renames as a
separate `git mv` step so history follows the file, verification of every output before that
file is written, a `failed` row with a reason instead of a partial edit, and an exit code a
script can trust. Check what the server you use offers for each of those.

## Using both from Claude Code or Copilot

Register an MCP server the way its own documentation describes. Configuration formats change
between releases, so this page links to each project in the table above rather than copying
its setup.

Call the CLI as a shell command. Install it with `dotnet tool install --global
AgenticRoslynTool`, or see the [README](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/README.md#install)
for the other two install options.

To tell the agent which to use, add a short section to `AGENTS.md` or `CLAUDE.md`:

```markdown
## Splitting C# files (SA1402)
- Use the Roslyn MCP server for questions about symbols and references. Use agentic-roslyn-tool for the split.
- Run `agentic-roslyn-tool split-types --input <list file> --dry-run --json --manifest <path outside the repo>` first. It changes no source file.
- Exit 0: read `summary`. If `summary.split` is 0, there is nothing to do. Exit 1: read `reason` on each `failed` row.
- Exit 2: fix the arguments. Exit 3: read the `error:` line on standard error.
- Then run `--phase renames`, commit, run `--phase content` with the same `--input` and `--manifest`, and build.
```

Pass the same list file of original paths to all three phases. Capture it once, before the
renames phase:

```bash
git -c core.quotePath=false ls-files '*.cs' > files.txt
```

Do not pass a directory to all three phases. After renames, the content phase sees the
renamed file under its new name and skips it with `not present as split in plan manifest`.
The README's
[recommended workflow](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/README.md#recommended-workflow)
walks through them.

## Trade-offs

Most Roslyn MCP servers load a solution before they can answer, and a large solution takes time
and memory to load. It also offers the agent a broad tool list, and the agent has to pick
the right tool for each step. In return it answers questions this CLI never can.

This CLI does one job. It cannot rename a symbol, find a reference, or tell you what a type
resolves to, because it reads syntax only. In return it has no solution to load, needs no
build to succeed first, and runs the same way in CI as on a laptop.

## FAQ

**Is agentic-roslyn-tool an MCP server?**
No. It is a .NET command line tool that agents call through the shell. This repository
ships no MCP wrapper.

**Which Roslyn MCP server is most used?**
As of 2026-10-11, glider has the most NuGet downloads in the table above, at 140,532.
SharpToolsMCP has the most GitHub stars, at 225. Neither number measures quality, and this
page makes no "best" call.

**Can an MCP server split all my files into one type per file?**
Some can. RoslynMcpServer's `move_type_to_file` has an `allFiles` mode, and sharplens-mcp
has a generation-only `move_type_to_file`. Check the tool list of the server you use. See
[Can an MCP server split every file?](#can-an-mcp-server-split-every-file) above.

**Do I need a semantic model to split types?**
No. Moving a top-level declaration and the comments it owns is a syntax operation. That is
why this CLI never loads the solution, and why it runs on code that does not build.

**Can I wrap the CLI as an MCP tool?**
Yes. Any MCP host that can run a command can call it, and the JSON report and exit codes
are a stable contract to build on. This repository ships no such wrapper.

## See also

- [README](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/README.md): install,
  usage, and the recommended workflow.
- [docs/behavior-contracts.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/behavior-contracts.md):
  what each phase and each guard promises.
- [docs/one-type-per-file-without-resharper.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/one-type-per-file-without-resharper.md):
  moving every C# type into its own file without ReSharper.
- [docs/verification.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/verification.md):
  what each check before a write proves, and what it does not.
- [docs/decision-log.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/decision-log.md):
  why the tool is a syntax-only command line tool.
- [Model Context Protocol](https://modelcontextprotocol.io)
- [The .NET Compiler Platform SDK (Roslyn)](https://learn.microsoft.com/dotnet/csharp/roslyn-sdk/)
