# AgenticRoslynTool

[![NuGet](https://img.shields.io/nuget/v/AgenticRoslynTool)](https://www.nuget.org/packages/AgenticRoslynTool)
[![NuGet downloads](https://img.shields.io/nuget/dt/AgenticRoslynTool)](https://www.nuget.org/packages/AgenticRoslynTool)
[![CI](https://github.com/rjmurillo/agentic-roslyn-tool/actions/workflows/ci.yml/badge.svg)](https://github.com/rjmurillo/agentic-roslyn-tool/actions/workflows/ci.yml)

AgenticRoslynTool splits C# files into one class per file across a whole repository, so you can fix StyleCop SA1402 without ReSharper or Visual Studio. Its `split-types` command gives each top-level type (class, struct, record, interface, enum, delegate) its own file.

It is not an MCP server. For when to use a Roslyn MCP server instead, see [docs/roslyn-mcp-vs-cli.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/roslyn-mcp-vs-cli.md).

## Why this exists

`dotnet format` cannot fix SA1402 across a repository. `SA1402CodeFixProvider` does not implement Fix All. As a result, this command succeeds without changing files:

```powershell
dotnet format analyzers --diagnostics SA1402
```

Production testing confirmed this behavior before development started. This tool provides the missing repository-wide operation.

Full walkthrough: [docs/fix-sa1402-whole-repo.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/fix-sa1402-whole-repo.md).

## Install

The tool ships as a .NET tool package on NuGet and needs the .NET 10 SDK.

Run it once without installing:

```powershell
dnx AgenticRoslynTool -- split-types --help
```

The `--` matters. Without it, `dnx` reads options such as `--help` as its own.

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

The examples below use `agentic-roslyn-tool`. Substitute `dnx AgenticRoslynTool --`
or `dotnet agentic-roslyn-tool` if you picked one of the other two.

## Build from source

```powershell
dotnet build
dotnet run --project src/AgenticRoslynTool -- split-types --help
```

## Usage

Start with `--dry-run`. Without `--phase` or `--dry-run`, the tool runs the `content`
phase, which rewrites files. Run `plan`, then `renames`, then `content`, as
[docs/fix-sa1402-whole-repo.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/fix-sa1402-whole-repo.md) walks through.

`split-types` requires `--input`, which identifies the C# files to process. It accepts a
directory to scan, a single `.cs` file, a CSV with a `file` column, a text file with one
path per line, or `-` to read paths from standard input.

Use these options to control the operation:

| Option | Purpose |
| --- | --- |
| `--phase plan\|renames\|content` | Selects a phase. Defaults to content, which rewrites files. |
| `--dry-run` | Runs the `plan` phase without changing source files. |
| `--input <dir\|file\|csv\|list\|->` | Supplies the required file list. |
| `--repo-root <path>` | Sets the repository root. |
| `--manifest <path>` | Sets the CSV manifest path. |
| `--json` | Prints one JSON report to standard output instead of CSV. |
| `--require-header <text>` | Prepends `<text>` to every emitted file and fails the split if any output does not start with it. Off by default. |
| `--exclude <path-substring>` | Skips any input whose path contains `<path-substring>`. Repeatable. Off by default. |

### Driving it from an agent

Point it at a tree and read the JSON. Nothing has to be written to disk first:

```powershell
agentic-roslyn-tool split-types --input src --dry-run --json
```

Discovery walks the directory for `.cs` files and skips `bin` and `obj`, because a build
owns those and recreates them. Piping works too, so a `git diff` or a `ripgrep` result can
feed the tool directly:

```powershell
git diff --name-only --diff-filter=d HEAD~1 |
  Select-String '\.cs$' |
  agentic-roslyn-tool split-types --input - --dry-run --json
```

Standard output carries a parseable document or nothing: the manifest CSV in `plan`, the
JSON report under `--json`, and nothing at all in `renames` and `content` without `--json`.
The manifest path, the one-line summary, and every error go to standard error, so a pipe
stays clean. The JSON report leads with counts:

```json
{
  "phase": "plan",
  "manifest": "C:\\repo\\sa1402-split-manifest.csv",
  "summary": { "total": 2, "split": 1, "skipped": 1, "failed": 0, "newFiles": 1 },
  "files": [ ... ]
}
```

Exit codes are distinct so a caller can branch without parsing anything:

| Code | Meaning |
| --- | --- |
| `0` | The run completed and nothing failed. A run that skips every file also exits 0, so read `summary` to learn whether work happened. |
| `1` | The run completed and at least one file failed. |
| `2` | The command line was wrong. |
| `3` | The run could not start or could not finish, for example a missing input file. |

Errors print as a single `error: <message>` line on standard error. No error path prints a
stack trace. `--version` prints the package version and exits 0.

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
Passing both `--dry-run` and `--phase` is an error, so argument order can never decide
between writing a manifest and rewriting source.

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

The content phase exits 3 if no plan manifest exists at `--manifest`, and overwrites that
manifest with its results. Copy the reviewed plan to a second path first when you want to
keep it for comparison.

The content phase moves each non-primary top-level type into its own file. Each type keeps its owned XML comments, attributes, and ordinary comments. Every emitted file also receives the source using directives and namespace declaration.

## Safety and verification

Before writing files, the tool:

* Detects filename collisions.
* Rejects splits that cross `#if` or `#region` boundaries unsafely.
* Verifies that each type retains its owned trivia.
* Checks line conservation across the split.

How each check works, what it proves, and what it does not:
[docs/verification.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/verification.md).

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

* Two input files can each plan a new file at the same output path. For example, two files in one folder each declare a type named `Shared` in different namespaces. The tool resolves that by qualifying both as `FileName.TypeName.cs`. This fallback conflicts with StyleCop SA1649. Fix just those files instead of disabling SA1649 for the whole repository: rename them by hand to `TypeName.FileName.cs`, move each into a folder named for its namespace, or suppress SA1649 for those paths in `.editorconfig`. (details: [docs/sa1402-sa1649-naming.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/sa1402-sa1649-naming.md#same-type-name-in-two-namespaces))
* Generic file names follow StyleCop's default `stylecop` file naming convention, such as `Foo{T}.cs`. Under the `metadata` convention, SA1649 expects ``Foo`1.cs`` instead. Every generic file the tool emits, whether `Foo{T}.cs` or a plain `Foo.cs`, then raises SA1649 unless its first type is `partial`. Rename those files by hand, or switch the repository to the `stylecop` convention. (details: [docs/sa1402-sa1649-naming.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/sa1402-sa1649-naming.md#if-you-use-the-metadata-convention))
* Two types that share a simple name inside a *single* file are not split. Unless they differ by generic arity or by type parameter names, which is resolved as `Name{T}.cs`, the file is skipped with `target path collision within split` and needs manual handling. (details: [docs/sa1402-sa1649-naming.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/sa1402-sa1649-naming.md))
* The tool does not prune using directives. A seemingly unused using can supply an extension method or target-typed conversion. Removing it would turn a mechanical edit into a semantic change.
* The tool uses the Roslyn Syntax API without a semantic model. It does not resolve types across projects.

## FAQ

**Can Visual Studio move all classes to separate files at once?** Not on its own: its quick action moves one type at a time, and [the one class per file guide](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/one-type-per-file-without-resharper.md) compares the bulk options.

**Is there a free alternative to ReSharper's Move Types Into Matching Files?** Yes, without ReSharper: CodeRush is a free Visual Studio extension, and this tool is free and needs no IDE, as [the guide](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/one-type-per-file-without-resharper.md) shows.

**Will it break my build?** It is a syntax-only move that copies every using and the namespace into each new file, so [build after the `content` phase](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/one-type-per-file-without-resharper.md#preview-rename-split) to confirm.

**Does `dotnet format` fix SA1402?** No. StyleCop's SA1402 code fix has no Fix All provider, so `dotnet format analyzers --diagnostics SA1402` exits 0 and leaves every file unchanged, as [the whole-repository guide](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/fix-sa1402-whole-repo.md) shows.

**Is this an MCP server?** No, it is a command line tool. From Claude Code or GitHub Copilot agent mode, run `agentic-roslyn-tool split-types --input src --dry-run --json` as a shell command, then read the JSON and the exit code, as [docs/roslyn-mcp-vs-cli.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/roslyn-mcp-vs-cli.md) explains.

## Documentation

| Document | What it covers |
|---|---|
| [AGENTS.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/AGENTS.md) | Orientation for AI agents and new contributors. Repository map, invariants that must not break, and traps that have already cost someone time. |
| [docs/behavior-contracts.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/behavior-contracts.md) | What each phase and each safety guard promises, including every skip and failure reason. |
| [docs/decision-log.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/decision-log.md) | Why the tool is built this way, and what breaks if a decision is reversed. |
| [docs/one-type-per-file-without-resharper.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/one-type-per-file-without-resharper.md) | How to move every C# type into its own file without ReSharper, with a worked example and a comparison of the options. |
| [docs/verification.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/verification.md) | How to verify a large split: what each check proves, and a before/after type scan you run yourself. |
| [docs/roslyn-mcp-vs-cli.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/roslyn-mcp-vs-cli.md) | When an agent needs a Roslyn MCP server and when it needs this command line tool. |
| [docs/sa1402-sa1649-naming.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/sa1402-sa1649-naming.md) | What file name SA1649 expects for generic and same-name types, and how to fix the names the tool cannot choose. |
| [docs/fix-sa1402-whole-repo.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/fix-sa1402-whole-repo.md) | Why `dotnet format` cannot fix SA1402, and how to split every multi-type file in a repository in three steps. |
| [CONTRIBUTING.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/CONTRIBUTING.md) | Build, test, commit conventions, and the CI gate. |

## Credit

[Practical Roslyn Syntax API refactoring](https://lizzy-gallagher.github.io/_site/roslyn-refactoring.html) influenced this project. It also favors the Syntax API because setup is simpler and its features usually suffice. This credit does not imply endorsement or contribution.

## License

This project uses the MIT License. See [LICENSE](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/LICENSE).
