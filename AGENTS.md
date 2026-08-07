# AGENTS.md

Entry point for AI agents working in this repository. Read this before you read any
source file. It tells you what the tool is, where each piece lives, what you are not
allowed to break, and how to prove your change is safe.

Humans should start with [README.md](README.md), which covers installation and usage.
This file covers the parts a fresh agent gets wrong.

## What this repository is

One command line tool. `AgenticRoslynTool split-types` splits C# files that declare more
than one top level type into one file per type, so a codebase can turn on StyleCop
SA1402.

It exists because `dotnet format` cannot do this job. `SA1402CodeFixProvider` does not
implement Fix All, so the formatter fixes one type and stops. Confirm this yourself
before you propose replacing the tool with a formatter run.

The tool uses the Roslyn Syntax API. It does not build a semantic model and does not need
a compiling solution. That is what lets it run over thousands of files in one pass.

## Repository map

| Path | What lives there |
|---|---|
| `src/AgenticRoslynTool/` | The entire product. 29 files. |
| `tests/AgenticRoslynTool.Tests/` | xUnit tests. 83 of them. |
| `docs/behavior-contracts.md` | What each phase and each guard promises. The semantic layer. |
| `docs/decision-log.md` | Why the tool is shaped this way, and what breaks if you undo it. |
| `.github/workflows/ci.yml` | The CI gate. Build and test on push and pull request. |

Every source file, so you do not have to open one to find out whether it matters:

| File | Responsibility |
|---|---|
| `FileSplitter.cs` | The orchestrator. Runs a phase over the inputs and turns one input into one result row. Delegates the actual work to the six types below it. Read this first. |
| `InputSource.cs` | Turns whatever the caller supplied, a directory, a list file, a manifest, standard input, into an ordered list of absolute paths. |
| `SplitPlanner.cs` | Decides what a split would do: which type stays, where each moved type lands, which inputs collide, which path a phase reads. Does not act. |
| `DirectiveAnalyzer.cs` | Decides whether preprocessor directives make a split unsafe. Returns a verdict, never acts on one. |
| `OutputBuilder.cs` | Renders the text of every output file: name, carved source, required header, trailing newline. |
| `OutputVerifier.cs` | The correctness checks, run against in-memory outputs before anything is written. A failure here means nothing reaches disk. |
| `WorkspaceWriter.cs` | The only place the tool touches the working tree. Every rename runs through `git mv`; every write preserves encoding and byte order mark. |
| `Program.cs` | Top level statements. Verb dispatch, error handling, exit codes, output selection. Declares no type. |
| `Options.cs` | Command line parsing and the option record. Add a new flag here. |
| `RunReport.cs` | Builds the `--json` report and the one-line summary from the report rows. |
| `RunOutcome.cs` | Splits a run into manifest rows and report rows. They differ only in the content phase. Read this before changing what the content phase writes. |
| `RunSummary.cs` | The aggregate counts inside that report. |
| `FileReport.cs` | One manifest row in JSON form. Field names mirror the CSV columns. |
| `NewFileReport.cs` | One created file inside a `FileReport`, carrying its path and type name. |
| `PathComparison.cs` | The path equality rule used for identity. Platform aware on purpose. |
| `Phase.cs` | The `plan`, `renames`, `content` enum. |
| `EncodedSource.cs` | Reads bytes into text while capturing encoding, byte order mark, and newline style, then writes them back unchanged. |
| `TopLevelType.cs` | One top level type declaration plus the file name it maps to. |
| `SplitPlan.cs` | The decision for one input: keep, rename, split, or refuse. |
| `PlannedFile.cs` | One output file inside a plan, before content is rendered. |
| `PlannedOutputEntry.cs` | A planned output path paired with its owning input, used for cross file collision detection. |
| `OutputFile.cs` | Rendered output. `BodyText` excludes any injected header; `Text` includes it. |
| `BlankLineCollapser.cs` | Collapses the blank line runs left behind when a type is removed. |
| `DirectiveInfo.cs` | One preprocessor directive and its span. |
| `DirectiveSafety.cs` | Verdict on whether directives make a file unsafe to split. |
| `FileResult.cs` | One manifest row. Owns the `split`, `skipped`, `failed` status vocabulary. |
| `NewFileResult.cs` | One created file inside a manifest row. |
| `ManifestWriter.cs` | Reads and writes the CSV manifest that carries state between phases. |
| `CsvFieldReader.cs` | Quote aware CSV field parsing for the manifest and for CSV inputs. |

The tool splits itself cleanly. Running it over `src/AgenticRoslynTool/` reports every file
skipped and exits 0. That is the cheapest smoke test in the repo, and it is how you confirm
an end to end change actually works.

## Build, test, and verify

```powershell
dotnet build -c Release      # must be 0 warnings, 0 errors
dotnet test  -c Release      # must be 83 passed, 0 failed
```

`TreatWarningsAsErrors` is on, so a warning is a build break. `EnforceCodeStyleInBuild`
is on as well. There is no separate lint step to run.

The SDK is pinned in `global.json` to 10.0.302 with `rollForward: latestMinor`. The
target framework is `net10.0`, set once in `Directory.Build.props`.

### End to end smoke test

Prove the tool still works on real files, not just that the unit tests pass:

```powershell
dotnet run --project src\AgenticRoslynTool -- split-types --input src\AgenticRoslynTool `
  --manifest "$env:TEMP\smoke-manifest.csv" --dry-run --json
```

Expect exit code 0, every row marked `skipped`, and `summary.failed` at 0. Point
`--manifest` outside the repository: the plan phase writes a manifest even under
`--dry-run`, and the default location is the repository root, which would leave an
untracked file behind.

## Rules you must not break

These are load bearing. Each one has a longer explanation with its reversal warning in
[docs/decision-log.md](docs/decision-log.md). Do not undo one because it looks like dead
weight.

1. **Never prune using directives.** A using that looks unused can still supply an
   extension method or a target typed conversion. Removing it turns a mechanical edit
   into a behavior change, which is the one thing this tool promises never to do.
2. **Remove types and collapse blank lines through Roslyn, never through text edits.** A
   blank line inside a block comment or inside an `#if` region lives in a token's trivia
   text, so a text level normalizer corrupts it. Header injection, the trailing newline,
   and line counting are deliberate string operations on whole file text and are fine;
   what must never become a string operation is deciding which type spans and which blank
   lines to remove.
3. **Verify before you write, per file.** For a single input, `VerifyOutputs` and
   `VerifyLineConservation` both run to completion before the first byte of that file is
   written, and a file that fails is never written and is recorded as `failed`. There is
   no run level transaction: `Run` processes inputs one at a time, so a failure on input
   50 leaves inputs 1 through 49 already rewritten on disk. Do not move a guard after the
   write, and do not describe the run as atomic.
4. **Line conservation counts `OutputFile.BodyText`, not `Text`.** `BodyText` is the
   output before any `--require-header` injection. Counting `Text` would let an injected
   header hide a dropped source line.
5. **Keep header normalization and header verification on the same value.**
   `NormalizeHeaderText` runs once, and both the injection and the `StartsWith` check use
   its result. When those two drifted apart, a header passed with `\n` against a CRLF
   source failed verification and silently refused to split every file.
6. **Preserve encoding, byte order mark, newline style, and trailing newline.**
   `EncodedSource` captures all four on read and reapplies them on write. Without this,
   every touched file shows up as a whole file diff.
7. **Types stay `internal`.** Tests reach them through `InternalsVisibleTo` in
   `src/AgenticRoslynTool/AgenticRoslynTool.csproj`. Never widen a type to `public` to
   make a test compile.
8. **Central Package Management is on.** Versions live in `Directory.Packages.props`. A
   `PackageReference` in a csproj must never carry a `Version` attribute.
9. **One top level type per file.** The tool obeys its own rule. A new type means a new
   file.
10. **Every path written in the content phase is existence checked first, with no
    exemptions.** The content phase substitutes output paths taken from the plan manifest,
    so the path finally written is not always the path the tool computed. Both are checked,
    and the substituted paths are also checked for uniqueness. Weakening either check lets
    an edited manifest overwrite an unrelated file or silently drop a type.
    `ManifestPathTamperingTests` pins both.
11. **No repository specific names in the tool.** Directories to skip come from
    `--exclude`, which defaults to empty, and a required file header comes from
    `--require-header`, which defaults to none. Two earlier versions hardcoded one
    employer's copyright text and one repository's directory layout. Both made the tool
    useless everywhere else.

## Conventions for anything you write

[CONTRIBUTING.md](CONTRIBUTING.md) owns the commit and style rules. The two that agents
break most often:

- **No em dashes and no en dashes**, anywhere. Not in code, comments, docs, commit
  messages, or pull request bodies. Use a comma, a period, or rewrite the sentence.
- **No generated file banners**, no timestamps, no "do not edit" headers, and no
  attribution to an AI tool in source or docs.

## Traps that have already caught someone

Each of these cost real debugging time in this repository. They are recorded so the next
agent does not pay again.

- **`#if DEBUG` is a poor test vehicle for "a directive spans two types".** Roslyn hides
  the guarded types from the parse tree, so the run short circuits on "one type or fewer"
  before directive safety is ever consulted. Use `#region` for that test instead.
- **A comment placed above a `using` is not the first type's trivia.** Roslyn attaches it
  to the `using` token, so it correctly appears in every output. To test that a comment
  travels with its type, put the comment immediately above the type declaration.
- **The content phase overwrites the plan manifest** when both use the default path. Pass
  an explicit `--manifest` if you want to keep the plan for comparison.
- **Exit codes are 0, 1, 2, 3, and they mean different things.** 0 the run completed with
  no failures, 1 a row is `failed`, 2 the command line was wrong, 3 the run could not
  complete. Skips are not failures, so a run that skips everything exits 0. Read
  `summary` from `--json`, or the summary line on standard error, to learn whether work
  happened.
- **Standard output is one document or nothing, standard error is everything else.** The
  manifest path, the summary line, and every `error:` message go to standard error so a
  caller can pipe standard output straight into a parser. Do not add chatter to standard
  output.
- **Standard input is read once per process.** A `plan` run and a `content` run are two
  processes, so a pipe has to be repeated. Reusing one `FileSplitter` instance for both
  phases in a test would see an empty stream on the second pass; use a list file there.
- **`actions/setup-dotnet` with `cache: true` fails the job when no `packages.lock.json`
  exists.** It globs for lock files and errors when the glob is empty. This repository
  has no lock files, so the cache option is deliberately absent from
  `.github/workflows/ci.yml`.
- **Keyword searching is not proof of absence.** The rollback logic in `WriteOutputs`
  never uses the words revert, restore, or rollback, so a grep for those terms reports
  that no rollback exists. It does exist. Read the code path.
- **Deleting a published GitHub release burns its version number permanently.** Immutable
  releases are enabled, so a tag name that a release has used can never be recreated, by
  you or by the workflow. Deleting the release and the tag does not free it, and turning
  the feature off does not either. `v0.1.0` was lost this way and the first published
  version is `v0.1.1`. The title and notes of a published release stay editable, so fix
  wording in place. Assets and the tag do not, so a wrong package means the next version.
- **A draft release does not create its tag.** GitHub files it under a placeholder
  `untagged-` name and creates the tag only when the draft is published. That is what
  makes the workflow's draft, upload, publish order safe to retry, and it was verified
  against this repository rather than assumed.

## Known issues, not yet fixed

Recorded so nobody rediscovers them and nobody assumes they are intentional.

- The header probe matches a multi-line banner literally, so an existing banner whose
  internal lines carry trailing spaces or tabs is not recognized and a second copy is
  injected. Trailing whitespace on the final banner line is tolerated. Matching every line
  loosely would need a line-by-line comparer, which is not worth it until a real file hits
  this.

- The manifest is both the plan and the run report. `Program` writes each run's rows back
  to the plan path, so a content run that fails a row replaces its `split` plan with
  `failed`, and a retry answers `not present as split in plan manifest`. Recovery is to
  re-run the plan phase. See the decision log for why this waits on a decision.
- `CsvFieldReader` exists, but not for the reason previously recorded here. The claim was
  that `Microsoft.VisualBasic.FileIO.TextFieldParser` forces a Windows only target. That
  was tested on this machine and is false: it compiles and parses quoted commas correctly
  on plain `net10.0`. The custom reader is kept because it works, is covered by seven
  tests, and carries no dependency on a Visual Basic compatibility API. Replacing it with
  `TextFieldParser` is a legitimate simplification nobody has measured on Linux.
- A stale plan manifest is detected structurally only. The manifest records paths and type
  names, not a content hash, so an edit that preserves both passes the mismatch check.

## Where to record what you learn

If you discover something a future agent needs, put it in the repository, not in a chat
reply or a pull request body. Neither survives.

| Kind of knowledge | Where it goes |
|---|---|
| A trap, an invariant, or a build rule | This file |
| What a phase or a guard promises | `docs/behavior-contracts.md` |
| Why a design choice was made, and what breaks if reversed | `docs/decision-log.md` |
| What a specific type or method does | An XML doc comment on that member |
