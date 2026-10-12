# AgenticRoslynTool agent context

AgenticRoslynTool is a .NET command line tool that splits C# files for StyleCop SA1402.
It is not an MCP server. Use a Roslyn MCP server for symbol and reference questions.
Use `agentic-roslyn-tool split-types` to split top-level types into separate files.

## Splitting C# files (SA1402)

- Save the original C# paths in one list file before running any phase. Use the same list file for every phase.
- Run `agentic-roslyn-tool split-types --input <list file> --dry-run --json --manifest <path outside the repo>` first. This changes no source file.
- Exit 0: read `summary`. If `summary.split` is 0, inspect skipped rows and their reasons. No automatic splits are planned. Exit 1: read `reason` on each `failed` row.
- Exit 2: fix the arguments. Exit 3: read the `error:` line on standard error.
- Run `--phase renames` with the same input and manifest, then commit the renames.
- Run `--phase content` with the same input and manifest, then build and test.

See the [recommended workflow](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/README.md#recommended-workflow) for complete commands.
