# AgenticRoslynTool agent context

AgenticRoslynTool is a .NET command line tool that splits C# files for StyleCop SA1402.
It is not an MCP server. Use a Roslyn MCP server for symbol and reference questions.
Use `agentic-roslyn-tool split-types` to split top-level types into separate files.
The same workflow ships as an Agent Skill at `skills/split-types/SKILL.md` in this package.

## Splitting C# files (SA1402)

- Save the original C# paths in one list file before running any phase. Use the same list file for every phase.
- Run `agentic-roslyn-tool split-types --input <list file> --dry-run --json --manifest <path outside the repo>` first. This changes no source file.
- Exit 0: read `summary`. If `summary.split` is 0, inspect skipped rows and their reasons. No automatic splits are planned. Exit 1: read `reason` on each `failed` row.
- Exit 2: fix the arguments. Exit 3: read the `error:` line on standard error.
- With `--json`, `nextArgs` in the report is the exact argument list for the next phase. Put your tool command in front of it and pass each element as its own argument. It is `null` when the run is finished, a row failed, nothing would split, or the input came from standard input. When it is `null`, check before you build: any `skipped` row whose reason starts with `stale plan:` needs a new plan, and `summary.failed` above 0 needs a fix and the same command again.
- Before `--phase content`, make sure `git status` shows no other changes, or ask the user first.
- If the repository enforces SA1649, add `--collision-naming type-first` to the plan run. Two inputs that emit the same file name then get `Shared.A.cs`, not `A.Shared.cs`. The plan records the value, and renames and content reuse it. A later phase given a different value exits 3 and touches nothing.
- Run `--phase renames` with the same input and manifest, then commit the renames.
- Run `--phase content` with the same input and manifest, then build and test.
- Only the plan phase writes the manifest. Renames and content write a report beside it, at the manifest path with `.report.csv` as its extension, or at `--report <path>`. Pass real paths, not symbolic links, for `--manifest` and `--report`.
- If content exits 1, fix the cause and run the same content command again. Do not re-run the plan. Rows already applied come back `skipped` with a reason starting `already applied`.
- Do not edit a planned file between phases. Each manifest row carries a `contentHash` of the file's bytes. A file that changed is `skipped` with a reason starting `stale plan:`. Undo the edit, or re-run the plan, to recover.
- Exit 3 naming a missing `contentHash` column, from renames or content, means an older version wrote the manifest. Re-run the plan.
- A `stale plan:` row stays refused while the file differs from the plan. Renames also refuses to split a file added to the input after the plan.

See the [recommended workflow](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/README.md#recommended-workflow) for complete commands.
