---
type: context-artifact
layer: semantic
subject: AgenticRoslynTool split-types
captured: 2026-08-04
captured_by: rjmurillo + AI-assisted analysis
methodology: Context Layer Generator from PromptKit v4 (https://promptkit.natebjones.com/20260402_795_promptkit_1)
sources:
  - src/AgenticRoslynTool/FileSplitter.cs
  - src/AgenticRoslynTool/Program.cs
  - src/AgenticRoslynTool/Options.cs
  - tests/AgenticRoslynTool.Tests/ (32 tests)
  - Production run in a closed-source repository, August 2026, which processed 7,818 files
    and split 461 of them. That result cannot be reproduced or verified from this repository.
---

# Behavioral Contracts

What each phase and each guard promises. Signatures tell you the shape of a call. This
file tells you what you can rely on after it returns.

Every claim here was read out of the source named in its section. If the code and this
document disagree, the code wins and this document is a bug.

## The command

```
agentic-roslyn-tool split-types --input <dir|file.cs|csv|list|-> [options]
```

| Contract | Value |
|---|---|
| Idempotency | Yes for `plan`. Yes for `content`: it never writes the plan it reads, and a row an earlier run applied is skipped as `already applied`. |
| Side effects | `plan` writes only the manifest. `renames` runs `git mv` and writes the report. `content` writes source files and the report. Neither of the last two writes the manifest. |
| Failure mode | Per file. An unexpected exception while reading, parsing, planning, or writing becomes a `failed` row and the run continues. So does a verification failure. Only a throw before the read path is resolved, a missing content manifest, a refused report path, or a report that could not be written ends the run with exit code 3. |
| Exit code | 0 no failures, 1 at least one row `failed`, 2 the command line was wrong, 3 the run could not complete. Skips are not failures. |
| Streams | Standard output carries a parseable document or nothing: the manifest CSV in `plan`, the JSON report under `--json`, and nothing in `renames` and `content` without `--json`, unless the report file could not be written, when the rows print as CSV. The manifest path, the report path, the one-line summary, and every error go to standard error. |
| Performance | Syntax parse only, no semantic model and no MSBuild load. A 7,818 file run completed in a single pass, measured outside this repository. |
| Data sensitivity | Reads and rewrites source files in place. No network calls. |
| Ordering | `plan` must run before `content`. `renames`, when used, belongs between them and must be committed first. |

`--help` and `-h` print usage on standard output and exit 0, in the verb position or in any
option position after `split-types`, and never depend on the rest of the command line being
valid. Bare `help` does the same, but only as the verb or as the first token after it: a
stray `help` later on the line is a rejected command line, since usage plus exit 0 there
would be a silent successful no-op. `--version` prints the package version and exits 0, in
either position. None of them is recognized in a value position, so `--input help` looks for
a file named `help`. No arguments, an unrecognized verb, or a bad option prints a one-line
`error: ...` on standard error, prints usage on standard error too, and exits 2. Any other
failure during the run prints one `error: ...` line and exits 3. No error path prints a
stack trace. One error path writes to standard output, and it writes a document: when the
report file cannot be written, the rows it would have held print there. See "Manifest and
report".

## Input sources

`--input` accepts five shapes, resolved in this order:

| Shape | Behavior |
|---|---|
| `-` | Reads newline-delimited paths from standard input. The manifest defaults into `--repo-root`. |
| An existing directory | Recursively discovers every `.cs` file beneath it, skipping any directory named `bin` or `obj` and not following directory symlinks. A file symlink is followed and split like any other file. The manifest defaults into `--repo-root`, not into the scanned tree. |
| A path ending in `.cs` | One file to split. It is never read as a list of paths. |
| A path ending in `.csv` | Reads the `file` column. A CSV without that column ends the run. |
| Anything else | Reads one path per line. |

Directory discovery skips `bin` and `obj` because a build owns those directories and
recreates their contents. That is a fact about how .NET lays out a build, not an opinion
about a repository, which is the line decision 14 draws around `--exclude`. Unlike an
`--exclude` match, a path that discovery never returns produces no manifest row, so it is
invisible to the caller. Pass a list file instead of a directory when you need a file under
a `bin` or `obj` segment. Everything discovered still passes through `--exclude`, so a
caller can narrow further.

Duplicate input paths are dropped, compared the way the running platform's filesystem
treats case: case-insensitively on Windows and macOS, case-sensitively elsewhere. On Linux
that keeps `Foo.cs` and `foo.cs` as the two separate files they are. The content phase keys
the plan manifest the same way, so a plan row can never be applied to a different file.
Collision detection deliberately stays case-insensitive on every platform, because
over-detecting a collision costs a skipped file while under-detecting one costs an
overwrite.

A directory scan that cannot read a subdirectory ends the run with exit code 3 rather than
returning a partial list. A partial scan would exit 0 and look exactly like a scan that
found nothing to do. Pass a list file when you need to scan around an unreadable directory.

`--dry-run` and `--phase` both set the phase, so supplying both is rejected with exit code
2 rather than letting argument order decide between writing a manifest and rewriting source.

A `split` row the content phase's input never supplied appears in the run report as
`skipped` with the reason `planned as split but not supplied to the content phase input`.
The content phase never writes the manifest, so the row is still `split` and still
actionable for the next batch.

An input the plan never covered is reported as `skipped` with the reason `not present as
split in plan manifest`. So `summary.total` counts what this run reported, which in the
content phase can exceed the number of paths supplied.

A file symlink whose target is also inside the scanned tree is de-duplicated by symlink
target, and the real path wins. Two paths naming one file would otherwise be split
twice, with the second pass reporting nothing to do.

Standard input is read once. A `plan` run and a `content` run are separate processes, so
each needs the path list piped to it again.

## JSON report

`--json` replaces the standard output document with one JSON object. Its field names and
its status vocabulary match the CSV manifest, so the two formats are one contract:

```json
{
  "phase": "plan",
  "manifest": "C:\\repo\\sa1402-split-manifest.csv",
  "report": null,
  "summary": { "total": 2, "split": 1, "skipped": 1, "failed": 0, "newFiles": 1 },
  "files": [
    {
      "originalPath": "C:\\repo\\src\\Foo.cs",
      "keptPath": "C:\\repo\\src\\Foo.cs",
      "gitMove": false,
      "status": "split",
      "reason": null,
      "note": null,
      "newFiles": [{ "newFilePath": "C:\\repo\\src\\Bar.cs", "type": "Bar" }]
    }
  ]
}
```

The CSV repeats the row once per new file; the JSON nests them under `newFiles` instead.
Both carry the same values. `summary` exists so a caller never has to tally rows to learn
whether work happened. `total` counts the rows this run reported. In `plan` that is one per
distinct input path. `manifest` is the plan manifest path in every phase. `report` is the
report file the run wrote, and is `null` in `plan`, which writes none.

## Manifest and report

Two CSV files with one format and two jobs.

| File | Written by | Read by | Holds |
|---|---|---|---|
| Manifest, `--manifest` | `plan` only | `content` | The reviewed plan. |
| Report, `--report` | `renames`, `content` | Nobody. It is for the caller. | What the last run did. |

- The report defaults to the manifest path with `.report.csv` as its extension, so
  `split-types.csv` pairs with `split-types.report.csv`. The default can never be the
  manifest.
- `--report` naming the manifest is rejected with exit code 2, compared without regard to
  case. So is `--report` with the `plan` phase, where it would do nothing.
- **The report path is checked before the run changes anything.** `ReportFile.Prepare`
  ends the run with exit code 3, and no source file touched, when the path is the
  manifest under another name, is itself a symbolic link, is a directory, sits in a directory that cannot take a new file, or names an existing file
  that is not a report. An existing file counts as a report when its first line starts
  with the `originalPath` column. An empty file does not. A path that ends in a directory
  separator is refused as a directory. So `--report` cannot replace the input list or a source
  file. A second plan manifest is in that format and would be replaced.
- **A manifest path that is a symbolic link is refused in `renames` and `content`.** The
  run ends with exit code 3 before any work. Pass the path of the file the link names.
  The `plan` phase still accepts a linked manifest path and writes through it.
- **A path that cannot be examined is refused.** The checks tell "nothing is there" from
  "the look failed", for example on a path longer than the operating system allows. A
  failed look ends the run with exit code 3. It is never read as an absent file.
- **Whether the report path is the manifest is observed, not derived.** The check creates
  a file beside the report path and looks for it in the manifest's directory. It is there
  exactly when the two are one directory, whatever linked directories either path runs
  through. The path is refused when the file names match without regard to case, unless
  the look proves the directories differ. A look that fails counts as the same directory. Neither path is itself a link by then, so nothing is resolved by
  hand.
- **The check changes no existing file.** It writes and removes one new file beside the
  report path, and never opens the report path for writing. It reads the first line of an
  existing file there to see whether it is a report. It creates the report's directory
  when that is missing, and leaves it there if the run then ends early.
- **The report is moved into place.** The rows go to a new file in the report's directory,
  which then replaces it. A reader never sees half a report. A report path that is a hard
  link to the manifest gets a new file, and the manifest keeps its bytes.
- **The check runs again at the write.** If the run created a file at the report path, for
  example `--report src/Beta.cs` where `Beta.cs` is a planned output, the report is not
  written and the source file is kept.
- **A report that cannot be written does not take the record with it.** The rows go to
  standard output: the JSON document under `--json`, with `report` set to `null`, and the
  CSV otherwise. So `report` is `null` in `plan`, and after a report that was not written. The summary prints, then `error: report not written to ...`, and the run
  exits 3.
- Each `renames` or `content` run that completes replaces the report. It is not a history.
  A run that ends with exit code 3 before it starts work leaves an earlier report as it
  was. Read the exit code first.
- The report holds the same rows `--json` prints under `files`. In `renames` those rows
  are the recomputed plan, with `failed` on a rename that did not land. They count splits
  and new files the content phase has yet to create.
- Verified by `SeparateRunReportTests`, which runs the real executable.

## Phase contracts

### plan

Reads the input list, computes the split for every file, writes a CSV manifest, and
prints it to standard output.

Promises:

- **No source file is written.** Verified by `PlanPhaseTests`, which asserts the input
  bytes are unchanged and that no output file appeared on disk.
- Every input appears in the manifest with status `split`, `skipped`, or `failed`, except
  that inputs repeating an earlier path are dropped by a de-duplication that follows the
  platform's filesystem case rules, and produce no row.
- The manifest round trips through `ManifestWriter.Read`.

You can run `plan` as many times as you like. It is the review step, and it is the reason
a human or an agent can inspect the whole change before a single byte moves.

### renames

Runs `git mv` for files whose name does not match their primary type.

Promises:

- **Neither reads nor writes the manifest.** It recomputes the plan from the files as they
  are, and writes its rows to the report. Run it with no plan on disk and no manifest
  appears, so `content` still exits 3 until `plan` has run. `--manifest` anchors the default
  report path here. It is still checked: a linked manifest path is refused, and so is a
  report path that names the same file.
- **A rename onto a directory is a `failed` row.** `git mv` would move the file into a
  directory that sits at the destination, so the phase refuses before it asks git.
- **A rename that fails is a `failed` row.** The run continues, writes the report, and
  exits 1. The row's `keptPath` says where the file is, which is the original path unless
  `git mv` landed before a later step failed. `gitMove` stays true, because the plan still
  asks for the move. The reason starts with `rename to <kept path> failed:`.

- Only touches rows the plan marked `split` with `GitMove` true.
- `FileSplitter.ApplyRenameOnly` re-reads the moved file and throws if `git mv` changed
  its content.
- Kept as a separate phase so the rename lands in its own commit. Git then records a
  rename instead of a delete plus an add, and the file history survives.

Recomputes the plan and reads no manifest. So it cannot notice a `--collision-naming`
value that differs from the plan run. It does not write the manifest either, so the
reviewed collision names stay as planned, and the content check still compares against
them. Pass the same value here as in the plan run, so the report shows the same paths.

Requires a real git repository. Most of the test suite avoids this phase by keeping every
fixture file name equal to its primary type name. One test,
`RenameFailure_IsAFailedRowInTheReport`, asks for a rename outside a repository to pin the
failed row.

### content

The default. Reads the plan manifest and performs the split.

Promises:

- **Acts only on rows the plan marked `split`.** Anything else is skipped with the reason
  `not present as split in plan manifest`.
- **Finds a renamed file by its kept path.** A directory scan after the renames phase
  supplies the kept path, not the original. An input that equals the kept path of exactly
  one `split` row with a git move is applied as that row. An original path match wins over
  a kept path match, and a kept path two rows claim matches neither. A row reached through
  both of its paths in one run is applied once. Every rename state and manifest check still
  runs on the row.
- Throws when the plan manifest does not exist. The message names the missing path. This
  one ends the whole run, not just the file.
- Refuses a file when the recomputed plan disagrees with the manifest it was handed. The
  comparison is structural: it covers the kept path, the git move flag, and the set of
  output paths. The manifest carries no content hash, so an edit that leaves type names
  and paths alone passes this check. Treat it as drift detection on the plan shape, not as
  proof the source is unchanged.
- Refuses a row the plan phase resolved under the `--collision-naming` value this run
  was not given. Two things must hold: the output path has the other value's qualified
  form, and the row's note records that the collision pass chose that path. The reason is
  the same `content phase plan does not match manifest`, and the skipped row's note names
  the path and both values. The content phase never reruns the collision pass, so this
  check is the only thing that keeps the flag in step with the plan. A path typed into the
  manifest by hand has no such note and is accepted as before. An edit that rewrites the
  note to name the new path as well is refused, because it then reads as the plan's own. The skipped row goes to the report
  and the plan keeps its `split` row, so recovery is to run the content phase again with
  the value the plan was given.
- **Never writes the manifest.** The plan is byte identical before and after a content
  run, whether rows succeed or fail. Results go to the report.
- **Can be retried against the same plan.** A row that failed is still `split` in the
  plan, so the next run applies it once the cause is fixed.
- **Skips a row an earlier run already applied.** The row is `skipped` with the reason
  `already applied: every planned output exists and this file no longer declares the
  planned types`. Both halves are checked, and the file must still declare a type.
  Nothing is written. A source that still declares a planned type beside an existing
  output is not applied work. It gets the existing `target path already exists` refusal.
  Verified by `ContentRetryTests`. Callers may match on the leading `already applied`.
- **Does not detect an interrupted write.** A process killed between the files of one
  split can leave a kept file with no planned type and a planned output missing. A retry
  reports that file as `nothing to split`. This predates the report file. Check
  `git status` after any run that did not exit normally.

## Guard contracts

These run in order inside `FileSplitter.Process`, and all of them run before any write for
that file.

### Input rejection, before parsing

Reason text shown in italics is a template; the run substitutes real values.

| Condition | Result | Reason text |
|---|---|---|
| Path contains an `--exclude` pattern | skipped | `excluded by pattern: <pattern>` |
| File missing | skipped | `input file does not exist` |
| File exists but cannot be read | failed | `cannot read input: <message>` |
| Parse produced an error diagnostic | failed | `input has syntax errors: <diagnostics>` |
| File has top level statements | skipped | `contains top-level statements; manual split required` |
| One type or fewer | skipped | `nothing to split: input has <n> top-level type declaration(s)` |
| Any `file` scoped type | skipped | `contains file-local type; manual split required` |
| Anything else that throws while handling one input | failed | the exception message |

The last row is the catch all, and it is deliberate. The content phase rewrites files as it
goes and writes the report once, at the end, so an exception that escapes one input strands
every file already rewritten in that run with no record of them. Every failure on one input
therefore becomes a row. Two states reach that catch without being a read, decode, or write
failure: an inconsistent rename state, where the plan asked for a `git mv` that was never
applied or was applied while the original path still exists, and a manifest edited into a
shape the plan phase never produces, such as two rows for the same type.

The exclusion check runs first, before the existence check, so an excluded path does not
have to exist. Exclusion patterns come only from `--exclude`, which defaults to empty.
The tool ships with no built in opinion about which directories hold generated code.

Two matching caveats. Backslashes in a pattern and in a path are both treated as directory
separators, so on Linux a file name containing a literal backslash can match a pattern that
was meant to name a directory. Matching is case insensitive even on a case sensitive
filesystem. Both errors produce an unwanted skip, never an unwanted write.

A file that does not parse is a failure, not a skip, because the tool cannot tell whether
it is safe to leave alone.

### DirectiveSafety

Refuses the split when a preprocessor directive region spans more than one top level
type. Splitting such a file would leave an `#if` open in one output and its `#endif`
stranded in another.

A directive wholly contained inside a single type is allowed and travels with that type.

`FileSplitter.ProducesEmptyDirectiveShell` is a second, later check. It refuses a split
that would leave a directive group with no code inside it.

### BuildPlan

Two refusals here are what make the plan then apply workflow trustworthy:

1. **A target path that already exists on disk is refused**, with the reason
   `target path already exists: <path>`. The check runs twice, and both runs matter. It
   runs against the path the tool computes from the type name, and again against the path
   substituted from the plan manifest, because the content phase writes the substituted
   path. The second run has no exemptions: a legitimate planned path never equals the
   original or the read path, so exempting them would only let an edited manifest aim a
   write at the file being read. The substituted paths are also checked for uniqueness
   against each other and against the kept path, because two rows pointing at one path
   would silently drop a type when the second write lands.
2. **A recomputed plan that disagrees with the supplied manifest refuses that file.** The
   row is `skipped` and no output is written for it, so a reviewed plan is applied or
   nothing is. Other files in the run are unaffected. See the structural limit noted
   under the `content` phase above.

Within a single input file, two types with the same simple name that do not differ by
generic arity are refused with `target path collision within split: <path>`. A type with
the kept type's name and type parameters targets the kept path, so two parts of one
partial type, generic or not, are refused this way even when the kept type keeps the
original file name. The same reason covers two manifest rows that resolve to one path.

### VerifyOutputs

Runs before `WriteOutputs`. Throws, and the file is recorded `failed` and never written,
when any of these hold:

- An output does not start with the required header, when `--require-header` is set.
- An output does not parse.
- A type's declaration plus its owned trivia appears in more than one output, or in none.
- The set of top level types across all outputs does not equal the input set.

### VerifyLineConservation

The structural safety net. Counts non whitespace lines in the original and in the
outputs, then throws when:

- A non whitespace line was dropped.
- A non prologue line was duplicated.

"Prologue" means source lines outside every type's owned span, chiefly using directives
and the namespace declaration. Those legitimately appear in every output, so they are
exempt from the duplication check.

**It counts `OutputFile.BodyText`, not `OutputFile.Text`.** `BodyText` is the output
before any `--require-header` injection. Counting `Text` would let an injected header line
mask a dropped source line, which defeats the guard.

### WriteOutputs

The only method that writes source files.

- Each created path is recorded **before** the write is attempted, so a file that fails
  partway through is still cleaned up.
- On any exception it deletes the files it created and restores the original file from
  the bytes read at the start. For a row whose rename already landed, the caller passes
  the kept path, so the restore writes there and no `git mv` is undone. `WriteOutputs`
  never moves a file.

### Encoding round trip

`EncodedSource` captures the encoding, whether a byte order mark was present, the newline
style, and whether the file ended with a newline. `WriteEncoded` reapplies all four.

Two limits worth knowing before you rely on this:

- **Encoding detection recognizes three byte order marks**: UTF-8, UTF-16 little endian,
  and UTF-16 big endian. Anything else is decoded as strict UTF-8, which throws on invalid
  bytes rather than substituting replacement characters. A file in a legacy code page is a
  hard failure, not a silent corruption.
- **Newline detection returns the first style it finds**, not the most common one. A file
  with mixed endings is normalized to whichever appeared first. A file with no newline at
  all falls back to `Environment.NewLine`, which is platform dependent.

The promise is that a file the tool touches shows a diff only where content actually
changed, never a whole file diff caused by a newline or a byte order mark flip.

### Header handling

Off unless `--require-header <text>` is passed.

- `NormalizeHeaderText` runs once and rewrites the header to the source file's own newline
  style. **The same normalized value feeds both the injection and the verification check.**
- `EnsureHeader` does not add the header when the text already begins with it, so a file
  that already carries the banner keeps its existing spacing and does not get a second
  copy.
- The probe is `StartsWithHeader`, anchored at the end of a line, so `// B-extra` does not
  satisfy a required `// B`. Spaces and tabs after the banner line are tolerated. Injection
  and verification call the same predicate, so a banner cannot be declared present and then
  rejected for being absent.

## Critical behavioral rules

The things that would surprise a reader who only skimmed the signatures.

1. **Using directives are never pruned.** By design. See [decision-log.md](decision-log.md).
2. **Verification is complete before the first write of that file.** There is no partially
   verified file. There is also no run level transaction: inputs are processed one at a
   time, so a failure late in a run leaves every earlier file already rewritten.
3. **Skips are not failures.** A run where every file is skipped exits 0. Read the
   `summary` counts (from `--json`, or the summary line on standard error) rather than the
   exit code alone when you care whether work happened.
4. **Only the plan phase writes the manifest.** `renames` and `content` write the report,
   so a failed content row can be retried against the plan that was reviewed.
5. **A cross file name collision is usually resolved to `FileName.TypeName.cs`**, which
   satisfies SA1402 but violates SA1649, so those files need a human decision afterwards.
   `--collision-naming type-first` resolves it to `TypeName.FileName.cs` instead, which
   passes SA1649. Under either value, when the qualified names collide too, the tool gives
   up and skips every input involved with
   `qualified output path collision requires manual handling`. The plan does not probe the
   disk for a qualified name. The content phase does, and refuses with
   `target path already exists`. Under `type-first`, an input whose qualified name would
   end in `.g`, `.g.i`, `.designer`, or `.generated` is skipped with
   `qualified output name would be read as generated code; manual handling required`.
6. **The tool trusts the plan manifest as a review artifact**, and refuses to proceed when
   reality has drifted from it. That refusal is a feature.
