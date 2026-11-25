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

Without `--phase`, the tool runs the `plan` phase, which changes no source file. Only
`--phase content` rewrites files. Version 0.1.1 and earlier ran `content` by default, so
pass `--dry-run` or `--phase` explicitly in scripts. Run `plan`, then `renames`, then `content`, as
[docs/fix-sa1402-whole-repo.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/fix-sa1402-whole-repo.md) walks through.

`split-types` requires `--input`, which identifies the C# files to process. It accepts a
directory to scan, a single `.cs` file, a CSV with a `file` column, a text file with one
path per line, or `-` to read paths from standard input.

Use these options to control the operation:

| Option | Purpose |
| --- | --- |
| `--phase plan\|renames\|content` | Selects a phase. Defaults to `plan`, which changes no source file. |
| `--dry-run` | Same as `--phase plan`. Kept so older scripts still work. |
| `--input <dir\|file\|csv\|list\|->` | Supplies the required file list. |
| `--repo-root <path>` | Sets the repository root. |
| `--manifest <path>` | Sets the CSV plan manifest path. Only the `plan` phase writes it. |
| `--report <path>` | Sets where `renames` and `content` record what they did. Defaults to the manifest path with `.report.csv` as its extension. Rejected with the `plan` phase, and rejected when it names the manifest. Replaces an existing file in the report format, and refuses any other existing file. |
| `--json` | Prints one JSON report to standard output instead of CSV. |
| `--require-header <text>` | Prepends `<text>` to every emitted file and fails the split if any output does not start with it. Off by default. |
| `--exclude <path-substring>` | Skips any input whose path contains `<path-substring>`. Repeatable. Off by default. |
| `--collision-naming file-first\|type-first` | Names the outputs when two inputs would emit the same file name. `file-first`, the default, gives `A.Shared.cs`. `type-first` gives `Shared.A.cs`, which passes SA1649. The plan records it in the manifest, and renames and content reuse it. A later phase given a different value exits 3. |
| `--file-naming stylecop\|metadata` | Chooses the SA1649 convention for generic file names. `stylecop`, the default, gives `Foo.cs`, or `Foo{T}.cs` when another type in the file shares the name. `metadata` gives ``Foo`1.cs``. Pass the same value to every phase. |

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
One exception keeps a run's record from being lost: if the report file cannot be written,
its rows print to standard output and the run exits 3.
The manifest path, the report path, the one-line summary, a `Next:` line, and every error
go to standard error, so a pipe stays clean. The JSON report leads with counts and the next
step:

```json
{
  "phase": "plan",
  "manifest": "C:\\repo\\sa1402-split-manifest.csv",
  "report": null,
  "collisionNaming": "file-first",
  "summary": { "total": 2, "split": 1, "skipped": 1, "failed": 0, "newFiles": 1 },
  "nextArgs": ["split-types", "--phase", "content", "--input", "C:\\repo\\src", "--manifest", "C:\\repo\\sa1402-split-manifest.csv", "--repo-root", "C:\\repo", "--json"],
  "files": [ ... ]
}
```

`report` is the path of the report file the run wrote. It is `null` in the `plan` phase,
which writes the manifest and no report. It is also `null` when the report file could not
be written.

`nextArgs` is the exact argument list for the next phase. Put your tool command in front
of it, such as `agentic-roslyn-tool` or `dnx AgenticRoslynTool --`, and run it. It points
at `renames` only when a file needs a `git mv`; commit the renames before you run the
`content` step it gives next. It is `null` when the run is finished, when a row failed,
when nothing would split, when the input came from standard input, which a second run
cannot read again, and after a renames run that found no plan on disk. It is also
`null` when the report file could not be written. `null` does not mean the split is done:
the split is done when a `content` report has no failed row and every skipped row says
`not present as split in plan manifest` or starts with `already applied`. It repeats `--report` only when you
chose a path, and `--file-naming` when it is `metadata`. Pass each element as its own argument; do not join them into one string.

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

If you pass `--file-naming metadata` to the plan, pass it to the renames and content phases
too. The convention decides file names, so it has to be the same in all three. The content
phase skips a file whose manifest row was made under the other convention, and writes
nothing for it. The plan is unchanged, so run the content phase again with the right value.
The renames phase runs a rename only when the plan holds that same rename. Under the wrong
value it moves nothing, but it can leave out a rename the plan did ask for. The content phase then fails that file
and tells you to run the renames phase, which works once the value is right.

The renames phase recomputes the plan from the current files. It never writes the
manifest, and records what it did in the report file, `split-types.report.csv` here. It
is held to the reviewed plan when one exists. Three cases are skipped with a
`stale plan:` reason and not renamed: a file whose bytes no longer match the recorded
`contentHash`, a file it would split that the plan did not mark `split`, and a file it
would split that was added to the input after the plan. Re-run the plan phase and review
it again if the tree changed since your last plan.

`git add -A` stages every new file, including the manifest and the report when they sit
inside the repository. Keep both outside the tree, or unstage them, when you do not want
them in the commit.

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

The content phase exits 3 if no plan manifest exists at `--manifest`. The renames and
content phases both exit 3 if the manifest has no `contentHash` or `collisionNaming` column
because an older version wrote it, or if `--collision-naming` contradicts the value the plan
recorded. Re-run the plan phase to rebuild it. The content phase never writes the
manifest. It records what it did in the report file, which is `split-types.report.csv`
here, or the path you pass with `--report`. Each run that completes replaces the previous
report. The tool checks the report path before it changes anything. It exits 3 without
touching a source file when the path cannot be written, is a symbolic link, or names an
existing file that is not an earlier report. It also exits 3 when `--manifest` is a
symbolic link, so pass the path of the real file to `renames` and `content`.
[docs/behavior-contracts.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/behavior-contracts.md)
lists every refusal under "Manifest and report". Any file in the manifest format counts as an
earlier report, so do not point `--report` at a second plan you want to keep.

Because the plan stays as you reviewed it, a content run can be repeated. When a row
fails, fix the cause and run the same command again. Never edit a source file to get
past the tool: a failed check on a file your build compiles is a bug in the tool. Exclude
that file, run the plan again, and report it. The retry applies the rows that
failed. A row an earlier run already applied is reported as `skipped` with a reason that
starts with `already applied`, and its files are not written again. A file that changed
for any other reason since the plan is `skipped` with a `stale plan:` reason.

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
fails a check is left untouched on disk and recorded as `failed`, with the reason. The row
is in the manifest when the `plan` phase finds the failure, and in the run report when the
`content` phase does. If a write fails partway through a file set, the tool deletes the files it created
and restores the original from the bytes it read. The process exits non-zero when any file
fails.

There is no run level transaction. Files are processed one at a time, so a failure late in
a run leaves the earlier files already rewritten. Review the plan first, and commit in
phases.

The tool also refuses to write over a file that already exists, whether that path came
from its own naming rules or from the manifest, and refuses a file in the content phase
when the recomputed plan disagrees with the reviewed manifest.

The plan is tied to the bytes it was reviewed against. Every manifest row records a
`contentHash`, the SHA-256 of the input file's raw bytes, and the JSON report carries the
same field:

```csv
originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash
"/repo/src/Foo.cs","/repo/src/Foo.cs",false,"split","","","/repo/src/Bar.cs","Bar","sha256:5f2b0e9c41d7a8366c1e0b94d2f7a3158e6c0d4b9a7f31e2c58d06b4a19e73fc"
```

The renames phase hashes each file it reads again, and the content phase hashes each file
the plan marks `split`. Any difference, a changed method
body, a trailing space, a new line ending, a byte order mark, refuses that file with a
`skipped` row whose reason starts with `stale plan:`. Nothing is written for it, and the
other files in the run are unaffected. A `git mv` does not change bytes, so a renamed file
still matches. The refusal holds for as long as the file differs from the plan. The plan
is not rewritten, so every later renames or content run refuses the file the same way.
Undo the edit, or re-run the plan phase and review the new plan. One changed file is
not stale: a file an earlier content run of the same plan already split is reported as
`already applied`.

One case reads oddly. A second content run, or a renames run, over a file content already
split reports it as `stale plan:`, because content rewrote the kept file. The file is left
alone.

Blank-line handling uses Roslyn trivia nodes, not raw text. Blank lines inside block comments or `#if` regions belong to a token's trivia text. A text normalizer can corrupt that content, while trivia-level handling preserves it.

One production run processed 7,818 files and split 461 of them. Roslyn parsed every C# file in that repository before and after the operation. Both scans found 13,561 top-level types, with zero differences in namespace, name, arity, kind, modifiers, base types, or constraints. The full solution then built with zero warnings and zero errors under `TreatWarningsAsErrors`. That repository is closed source, so this result cannot be reproduced from here.

## Known limitations

* Two input files can each plan a new file at the same output path. For example, two files in one folder each declare a type named `Shared` in different namespaces. The tool resolves that by qualifying both as `FileName.TypeName.cs`. This default conflicts with StyleCop SA1649. Pass `--collision-naming type-first` to the plan phase to get `TypeName.FileName.cs` instead, which SA1649 accepts. A generic name such as `Shared{T}.A.cs` passes under the `stylecop` convention only. If you already split with the default, fix just those files instead of disabling SA1649 for the whole repository: rename them by hand to `TypeName.FileName.cs`, move each into a folder named for its namespace, or suppress SA1649 for those paths in `.editorconfig`. (details: [docs/sa1402-sa1649-naming.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/sa1402-sa1649-naming.md#same-type-name-in-two-namespaces))
* Generic file names follow StyleCop's default `stylecop` file naming convention, such as `Foo{T}.cs`. Under the `metadata` convention, SA1649 expects ``Foo`1.cs`` instead. Pass `--file-naming metadata` to every phase and the tool plans that name for each generic type it moves or keeps. The tool does not read `stylecop.json` or `.editorconfig`, so without the flag every generic file it emits raises SA1649 in a `metadata` repository unless its first type is `partial`. A file that holds one generic type only is not split, so the flag does not rename it. (details: [docs/sa1402-sa1649-naming.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/sa1402-sa1649-naming.md#if-you-use-the-metadata-convention))
* Two types that share a simple name inside a *single* file are not split. Unless they differ by generic arity or by type parameter names, which is resolved as `Name{T}.cs`, the file is skipped with `target path collision within split` and needs manual handling. Under `--file-naming metadata` only arity tells them apart: `Box<T>` and `Box<U>` both map to ``Box`1.cs``, so that file is skipped too. (details: [docs/sa1402-sa1649-naming.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/sa1402-sa1649-naming.md))
* Two or more parts of one partial type in the same file, generic or not, make the tool skip that file with `target path collision within split`. The parts are not merged, and other types in that file stay where they are.
* A type declared with the `file` modifier never leaves its file, and that file is never renamed, because the compiler builds the type's metadata name from the file's name. The tool moves the ordinary types out and leaves the `file` type with the kept type. SA1402 then still flags that file, once for each `file` class or record when the kept type is a class or record too, and the row's `note` says so. Suppress it for that file in `.editorconfig`, or with a pragma. The file is skipped when a type that would move names a `file` type, when a `file` type declares extension members, when a using directive names one, when a type lists one among its base types, when the file has an excluded `#if` branch, a `global using` directive, or a `#line`, `#nullable`, `#pragma`, `#define`, or `#undef` directive on or inside a type or on an assembly attribute, or when it has only one other type. (details: [docs/fix-sa1402-whole-repo.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/fix-sa1402-whole-repo.md#files-that-hold-a-file-type))
* The tool does not prune using directives. A seemingly unused using can supply an extension method or target-typed conversion. Removing it would turn a mechanical edit into a semantic change.
* The tool uses the Roslyn Syntax API without a semantic model. It does not resolve types across projects.
* A file with syntax errors and named preprocessor conditions is skipped. The build may parse it differently.
* A .NET 10 file-based program, a file with `#:` or `#!` lines, is never split. It compiles alone, so a type moved to a new file would leave the program. A file run with `dotnet run --file` that has neither line looks like project code to the tool, so leave it out of the input list.
* `dotnet new` template sources, such as files with `#if (UseXunit)`, are not C# until the template engine runs. They can fail or be skipped. Pass `--exclude` for the template folder.
* A `failed` row on a file your build compiles is a bug in the tool. Exclude that file and [open an issue](https://github.com/rjmurillo/agentic-roslyn-tool/issues). Do not edit the file to get past the tool.

## FAQ

**Can Visual Studio move all classes to separate files at once?** Not on its own: its quick action moves one type at a time, and [the one class per file guide](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/one-type-per-file-without-resharper.md) compares the bulk options.

**Is there a free alternative to ReSharper's Move Types Into Matching Files?** Yes, without ReSharper: CodeRush is a free Visual Studio extension, and this tool is free and needs no IDE, as [the guide](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/one-type-per-file-without-resharper.md) shows.

**Will it break my build?** It is a syntax-only move that copies every using and the namespace into each new file, so [build after the `content` phase](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/one-type-per-file-without-resharper.md#preview-rename-split) to confirm.

**Does `dotnet format` fix SA1402?** No. StyleCop's SA1402 code fix has no Fix All provider, so `dotnet format analyzers --diagnostics SA1402` exits 0 and leaves every file unchanged, as [the whole-repository guide](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/fix-sa1402-whole-repo.md) shows.

**Does the package ship an Agent Skill?** Yes. The package holds `skills/split-types/SKILL.md`, which tools such as `dotnet-inspect` list. A tool package cannot be a `PackageReference`, so tools that scan a project's packages do not find it. [docs/agent-skills-in-nuget.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/agent-skills-in-nuget.md) shows where it lands and how to load it.

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
| [docs/agent-skills-in-nuget.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/agent-skills-in-nuget.md) | The Agent Skill in the NuGet package: where it lands, what finds it, and how to check it. |
| [docs/sa1402-sa1649-naming.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/sa1402-sa1649-naming.md) | What file name SA1649 expects for generic and same-name types, and how to fix the names the tool cannot choose. |
| [docs/fix-sa1402-whole-repo.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/docs/fix-sa1402-whole-repo.md) | Why `dotnet format` cannot fix SA1402, and how to split every multi-type file in a repository in three steps. |
| [CONTRIBUTING.md](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/CONTRIBUTING.md) | Build, test, commit conventions, and the CI gate. |

## Credit

[Practical Roslyn Syntax API refactoring](https://lizzy-gallagher.github.io/_site/roslyn-refactoring.html) influenced this project. It also favors the Syntax API because setup is simpler and its features usually suffice. This credit does not imply endorsement or contribution.

## License

This project uses the MIT License. See [LICENSE](https://github.com/rjmurillo/agentic-roslyn-tool/blob/main/LICENSE).
