# AgenticRoslynTool

AgenticRoslynTool is a .NET console tool for mechanical, verifiable C# refactoring. Its `split-types` command enforces StyleCop SA1402 by placing each top-level type in a separate file.

## Why this exists

`dotnet format` cannot fix SA1402 across a repository. `SA1402CodeFixProvider` does not implement Fix All. As a result, this command succeeds without changing files:

```powershell
dotnet format analyzers --diagnostics SA1402
```

Production testing confirmed this behavior before development started. This tool provides the missing repository-wide operation.

## Install

The tool ships as a .NET tool package on NuGet and needs the .NET 10 SDK.

Run it once without installing:

```powershell
dnx AgenticRoslynTool split-types --help
```

Install it globally:

```powershell
dotnet tool install --global AgenticRoslynTool
agentic-roslyn-tool split-types --help
```

Install it into a repository so every contributor gets the same version:

```powershell
dotnet new tool-manifest        # only if the repository has no manifest yet
dotnet tool install AgenticRoslynTool
dotnet agentic-roslyn-tool split-types --help
```

The examples below use `agentic-roslyn-tool`. Substitute `dnx AgenticRoslynTool`
or `dotnet agentic-roslyn-tool` if you picked one of the other two.

## Build from source

```powershell
dotnet build
dotnet run --project src/AgenticRoslynTool -- split-types --help
```

## Usage

`split-types` requires `--input <csv-or-list>`. The input identifies the C# files to process.

Use these options to control the operation:

| Option | Purpose |
| --- | --- |
| `--phase plan\|renames\|content` | Selects one of the three phases. |
| `--dry-run` | Runs the `plan` phase without changing source files. |
| `--input <csv-or-list>` | Supplies the required file list. |
| `--repo-root <path>` | Sets the repository root. |
| `--manifest <path>` | Sets the CSV manifest path. |
| `--require-header <text>` | Prepends `<text>` to every emitted file and fails the split if any output does not start with it. Off by default. |
| `--exclude <path-substring>` | Skips any input whose path contains `<path-substring>`. Repeatable. Off by default. |

Use `--exclude` for directories a generator owns, so the tool records them as skipped
instead of rewriting output that will be regenerated anyway. Matching is case insensitive
and separator agnostic, so one pattern works on Windows and Linux:

```powershell
agentic-roslyn-tool split-types --input files.txt --exclude obj/ --exclude /generated/
```

Use `--require-header` when your repository mandates a file header, for example a
license or copyright line. Pass the exact text of the first header line:

```powershell
agentic-roslyn-tool split-types --input files.txt --require-header "// Copyright (c) Contoso."
```

The header is normalized to the newline style of each source file, so you can pass it with
either `\n` or `\r\n` line breaks, and multi-line headers are supported. A file that already
begins with the header keeps its existing spacing and is not given a second copy. Files that
do not have it receive the header followed by one blank line. Without the flag, the tool
emits no header and leaves each type's own leading comments attached to that type alone.

### Recommended workflow

Assume `files.txt` lists repository-relative C# paths. First, create and review a plan:

```powershell
agentic-roslyn-tool split-types `
  --phase plan `
  --input files.txt `
  --repo-root . `
  --manifest split-types.csv
```

`--dry-run` is an alias for the `plan` phase. The plan writes `split-types.csv` and changes nothing.

Next, rename files that do not match their primary type:

```powershell
agentic-roslyn-tool split-types `
  --phase renames `
  --input files.txt `
  --repo-root . `
  --manifest split-types.csv

git add -A
git commit -m "Rename C# files to match primary types"
```

Keep renames in their own commit. Git can then record renames instead of delete-and-add pairs, preserving file history.

The renames phase recomputes the plan from the current files rather than replaying the
reviewed manifest, and rewrites the manifest with what it did. Re-run the plan phase and
review it again if the tree changed since your last plan.

Finally, split files and review the result:

```powershell
agentic-roslyn-tool split-types `
  --phase content `
  --input files.txt `
  --repo-root . `
  --manifest split-types.csv

dotnet build
git diff --check
```

The content phase writes its own manifest to the same path it read, so passing the same
`--manifest` for both phases overwrites the reviewed plan. Use a second path when you want
to keep the plan for comparison.

The content phase moves each non-primary top-level type into its own file. Each type keeps its owned XML comments, attributes, and ordinary comments. Every emitted file also receives the source using directives and namespace declaration.

## Safety and verification

Before writing files, the tool:

* Detects filename collisions.
* Rejects splits that cross `#if` or `#region` boundaries unsafely.
* Verifies that each type retains its owned trivia.
* Checks line conservation across the split.

Every check for a given file runs before anything is written for that file. A file that
fails a check is left untouched on disk and recorded as `failed` in the manifest, with the
reason. If a write fails partway through a file set, the tool deletes the files it created
and restores the original from the bytes it read. The process exits non-zero when any file
fails.

There is no run level transaction. Files are processed one at a time, so a failure late in
a run leaves the earlier files already rewritten. Review the plan first, and commit in
phases.

The tool also refuses to write over a file that already exists, whether that path came
from its own naming rules or from the manifest, and refuses to run the content phase at
all when the recomputed plan disagrees with the reviewed manifest.

Blank-line handling uses Roslyn trivia nodes, not raw text. Blank lines inside block comments or `#if` regions belong to a token's trivia text. A text normalizer can corrupt that content, while trivia-level handling preserves it.

One production run processed 7,818 files and split 461 of them. Roslyn parsed every C# file in that repository before and after the operation. Both scans found 13,561 top-level types, with zero differences in namespace, name, arity, kind, modifiers, base types, or constraints. The full solution then built with zero warnings and zero errors under `TreatWarningsAsErrors`. That repository is closed source, so this result cannot be reproduced from here.

## Known limitations

* Two input files in different namespaces can produce the same output filename. The tool resolves that by qualifying both as `FileName.TypeName.cs`. This fallback conflicts with StyleCop SA1649. Disable SA1649 in repositories that need this naming form.
* Two types that share a simple name inside a *single* file are not split. Unless they differ only by generic arity, which is resolved as `Name{T}.cs`, the file is skipped with `target path collision within split` and needs manual handling.
* The tool does not prune using directives. A seemingly unused using can supply an extension method or target-typed conversion. Removing it would turn a mechanical edit into a semantic change.
* The tool uses the Roslyn Syntax API without a semantic model. It does not resolve types across projects.

## Documentation

| Document | What it covers |
|---|---|
| [AGENTS.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/AGENTS.md) | Orientation for AI agents and new contributors. Repository map, invariants that must not break, and traps that have already cost someone time. |
| [docs/behavior-contracts.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/behavior-contracts.md) | What each phase and each safety guard promises, including every skip and failure reason. |
| [docs/decision-log.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/decision-log.md) | Why the tool is built this way, and what breaks if a decision is reversed. |
| [CONTRIBUTING.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/CONTRIBUTING.md) | Build, test, commit conventions, and the CI gate. |

## Credit

[Practical Roslyn Syntax API refactoring](https://lizzy-gallagher.github.io/_site/roslyn-refactoring.html) influenced this project. It also favors the Syntax API because setup is simpler and its features usually suffice. This credit does not imply endorsement or contribution.

## License

This project uses the MIT License. See [LICENSE](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/LICENSE).
