# AgenticRoslynTool agent context

AgenticRoslynTool is a .NET command line tool that splits C# files for StyleCop SA1402.
It is not an MCP server. Use a Roslyn MCP server for symbol and reference questions.
Use `agentic-roslyn-tool split-types` to split top-level types into separate files.

## Splitting C# files (SA1402)

- Save the original C# paths in one list file before running any phase. Use the same list file for every phase.
- Run `agentic-roslyn-tool split-types --input <list file> --dry-run --json --manifest <path outside the repo>` first. This changes no source file.
- Exit 0: read `summary`. If `summary.split` is 0, inspect skipped rows and their reasons. No automatic splits are planned. Exit 1: read `reason` on each `failed` row.
- Exit 2: fix the arguments. Exit 3: read the `error:` line on standard error.
- If the repository enforces SA1649, add `--collision-naming type-first` to every phase, renames included. Two inputs that emit the same file name then get `Shared.A.cs`, not `A.Shared.cs`. A content run whose value differs from the plan skips those files and exits 0. The plan is unchanged, so run content again with the plan's value.
- Run `--phase renames` with the same input and manifest, then commit the renames.
- Run `--phase content` with the same input and manifest, then build and test.
- Only the plan phase writes the manifest. Renames and content write a report beside it, at the manifest path with `.report.csv` as its extension, or at `--report <path>`. Pass real paths, not symbolic links, for `--manifest` and `--report`.
- If content exits 1, fix the cause and run the same content command again. Do not re-run the plan. Rows already applied come back `skipped` with a reason starting `already applied`.

See the [recommended workflow](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/README.md#recommended-workflow) for complete commands.
