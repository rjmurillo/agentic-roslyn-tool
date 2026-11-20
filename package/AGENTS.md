# AgenticRoslynTool agent context

AgenticRoslynTool is a .NET command line tool that splits C# files for StyleCop SA1402.
It is not an MCP server. Use a Roslyn MCP server for symbol and reference questions.
Use `agentic-roslyn-tool split-types` to split top-level types into separate files.

## Splitting C# files (SA1402)

- Save the original C# paths in one list file before running any phase. Use the same list file for every phase.
- Decide `--file-naming` before the first run. If the repository sets StyleCop `fileNamingConvention` to `metadata`, add `--file-naming metadata` to every command below, the dry run included. Generic files are then named ``Foo`1.cs``. Without the flag a generic file is named `Foo.cs`, or `Foo{T}.cs` when another type in the file shares the name.
- Never change `--file-naming` between phases. The content phase detects a mismatch and skips the file. The renames phase never runs a rename the plan did not ask for, but under another value it can leave out a planned one, and the content phase then fails that file. If you did change it, run the dry run again and review the new plan. If `git status` shows a rename from an earlier phase, first undo that rename with `git mv`, so the list file names paths that exist. A listed path that no longer exists is skipped with `input file does not exist`.
- Run `agentic-roslyn-tool split-types --input <list file> --dry-run --json --manifest <path outside the repo>` first. This changes no source file.
- Exit 0: read `summary`. If `summary.split` is 0, inspect skipped rows and their reasons. No automatic splits are planned. Exit 1: read `reason` on each `failed` row.
- Exit 2: fix the arguments. Exit 3: read the `error:` line on standard error.
- If the repository enforces SA1649, add `--collision-naming type-first` to every phase, renames included. Two inputs that emit the same file name then get `Shared.A.cs`, not `A.Shared.cs`. A content run whose value differs from the plan skips those files and exits 0. The plan is unchanged, so run content again with the plan's value.
- Run `--phase renames` with the same input and manifest, then commit the renames.
- Run `--phase content` with the same input and manifest, then build and test.
- Only the plan phase writes the manifest. Renames and content write a report beside it, at the manifest path with `.report.csv` as its extension, or at `--report <path>`. Pass real paths, not symbolic links, for `--manifest` and `--report`.
- If content exits 1, fix the cause and run the same content command again. Do not re-run the plan. Rows already applied come back `skipped` with a reason starting `already applied`.
- Do not edit a planned file between phases. Each manifest row carries a `contentHash` of the file's bytes. A file that changed is `skipped` with a reason starting `stale plan:`. Undo the edit, or re-run the plan, to recover.
- Exit 3 naming a missing `contentHash` column, from renames or content, means an older version wrote the manifest. Re-run the plan.
- A `stale plan:` row stays refused while the file differs from the plan. Renames also refuses to split a file added to the input after the plan.

See the [recommended workflow](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/README.md#recommended-workflow) for complete commands.
