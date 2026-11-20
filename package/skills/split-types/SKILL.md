---
name: split-types
description: >-
  Split C# files that declare more than one top-level type into one file per type, so a
  repository can enable StyleCop SA1402, and SA1649 with it. Use when dotnet format reports
  "Unable to fix SA1402", when a build fails on SA1402, or when asked for one type per file
  across many files. Runs the agentic-roslyn-tool .NET tool from the AgenticRoslynTool package.
license: MIT
compatibility: Needs the .NET 10 SDK, and git for the renames step. Windows, macOS, and Linux.
metadata:
  package: AgenticRoslynTool
---

# Split C# files for SA1402

`split-types` moves every extra top-level type into its own file. It parses with Roslyn
and does not need a solution that compiles. `dotnet format` cannot do this job, because the
SA1402 code fix has no Fix All. This skill ships inside the package, so it matches the
installed version.

Run the tool as `agentic-roslyn-tool` (global tool), `dotnet agentic-roslyn-tool` (local
tool), or `dnx AgenticRoslynTool --` (no install). Below, `<tool>` means whichever you use.

## Steps

1. List the C# files in a file outside the repository:
   `git -c core.quotePath=false ls-files '*.cs' > ../split.txt`
2. Plan. This changes no source file:
   `<tool> split-types --input ../split.txt --manifest ../split.csv --json`
   If the repository enforces SA1649, add `--collision-naming type-first` to this step only.
3. Read the JSON report. `summary.split` counts the files that will split. Each `skipped`
   row has a `reason`. If `summary.split` is 0, stop: there is nothing to do.
4. Run `nextArgs` from the report, with `<tool>` in front. Each run prints a new report
   with a new `nextArgs`. Repeat until `nextArgs` is `null`.
   After a `renames` step, commit the renames before you run the next step.
5. Build and run the tests.

## Exit codes

- `0`: no row failed. It can still mean nothing happened, so read `summary.split`.
- `1`: a row failed. Read `reason` on each `failed` row.
- `2`: the command line was wrong. Read the usage on standard error.
- `3`: the run could not complete. Read the `error:` line. Most say to re-run the plan.

## Rules

- Do not edit a planned file between steps. A changed file is skipped with a reason that
  starts `stale plan:`. Re-run the plan to recover.
- Do not edit the manifest. Renames and content read the plan from it, including the
  `collisionNaming` value.
- Keep every `using` the tool copied into a new file. One that looks unused can still
  supply an extension method.
- This is a command line tool, not an MCP server. For symbol and reference questions, use
  a Roslyn MCP server.
