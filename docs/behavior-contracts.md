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
| Time limit | Each `git mv` in `renames` gets 120 seconds. Past that, the tool kills git and its children and marks the row `failed`. The row's `reason` starts `rename to <path> failed: git mv did not exit within 120 seconds`. Its first sentence ends `and was killed`, or `and could not be stopped and may still be running` when git outlives the kill. It says a killed git can leave an `index.lock` file and a half-finished move, and includes git's standard error. A non-zero exit gives `git mv failed (exit <code>):` and git's message. Paths reach git as separate arguments, so quotes in a file name are safe. |
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

A run with no `--phase` and no `--dry-run` is the `plan` phase. It writes the manifest and
changes no source file. Only `--phase content` rewrites source, and only `--phase renames`
runs `git mv`. Version 0.1.1 and earlier defaulted to `content`. See decision 25.

`--dry-run` and `--phase` both set the phase, so supplying both is rejected with exit code
2 rather than letting argument order decide between writing a manifest and rewriting source.
`--dry-run` is now the same as the default and is kept so older scripts still parse.

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
  "collisionNaming": "file-first",
  "summary": { "total": 2, "split": 1, "skipped": 1, "failed": 0, "newFiles": 1 },
  "nextArgs": ["split-types", "--phase", "content", "--input", "C:\\repo\\src", "--manifest", "C:\\repo\\sa1402-split-manifest.csv", "--repo-root", "C:\\repo", "--json"],
  "files": [
    {
      "originalPath": "C:\\repo\\src\\Foo.cs",
      "keptPath": "C:\\repo\\src\\Foo.cs",
      "gitMove": false,
      "status": "split",
      "reason": null,
      "note": null,
      "newFiles": [{ "newFilePath": "C:\\repo\\src\\Bar.cs", "type": "Bar" }],
      "contentHash": "sha256:5f2b0e9c41d7a8366c1e0b94d2f7a3158e6c0d4b9a7f31e2c58d06b4a19e73fc"
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
| Manifest, `--manifest` | `plan` only | `renames`, `content` | The reviewed plan. |
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
- **A manifest path must end in a file name.** `--manifest` with a trailing directory
  separator is rejected with exit code 2 in every phase. A manifest path that is a
  directory ends `renames` and `content` with exit code 3. The name comparison below
  reads the last part of the path, so that part has to be the file's name.
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

`contentHash` is the ninth CSV column and the last field of each JSON file entry. It is
`sha256:` followed by 64 lowercase hexadecimal digits, computed over the raw bytes of the
input as the plan read them. It is empty in the CSV and `null` in the JSON when the plan
never read the file: an excluded path, a missing file, an unreadable one. Every other row
carries it, whatever its status. A run that starts a plan records the hash. The renames
and content phases copy it from the manifest row for that file and never report their own,
so a `stale plan:` row still shows the planned hash and names the hash it found in its
reason. New columns are appended, never inserted.

`collisionNaming` is the CSV column after `contentHash`, and a top level JSON field. It is
`file-first` or `type-first`: the value this run used, after resolving the flag against
the manifest. It is one value for the whole run. CSV has no place for a file level value,
so the CSV repeats it on every row, and every row must agree. JSON carries it once, at the
top, because a per-file copy would invite a reader to expect per-file differences. The
two formats differ in shape in three places: this one, the nesting of `newFiles`, and
`nextArgs`, which only JSON carries. See decision 26.

`nextArgs` is JSON only. It is the argument list for the phase that should run next, from
the verb onward, without the command name, because only the caller knows whether it ran
`agentic-roslyn-tool`, `dotnet agentic-roslyn-tool`, or `dnx AgenticRoslynTool --`. Each
element is one argument, so no shell quoting is involved. It sits before `files` so a
reader that truncates the document still sees it. The rules live in `NextStep.For`:

- After `plan`, it points at `renames` when any `split` row has `gitMove` true, and at
  `content` otherwise. A renames run with nothing to move would spend a turn.
- After `renames`, it points at `content`. Commit the renames first; the `Next:` line on
  standard error says so.
- It is `null` after `content`, when any row is `failed`, when no row is `split`, when
  `--input` was `-`, after a renames run with no plan manifest on disk, and when the report
  file could not be written. Standard input cannot be read again by a second process, so
  replaying `--input -` would plan nothing and exit 0. Save the paths to a list file
  instead. A renames run with no manifest plans afresh, but content exits 3 without one,
  so suggesting content there is sure to fail.
- `null` does not mean the split is done. The packaged skill says the split is done only
  when the last report's `phase` is `content`, `summary.failed` is 0, and every skipped
  row's reason is `not present as split in plan manifest` or starts with `already
  applied`. Those are the only content skips that leave no planned work behind. A repeated
  renames run can end with `null` `nextArgs`, for example when every split row was a
  rename and now says `input file does not exist`, while content has not run. A content run without the plan's `--file-naming`
  ends with `null` and a plan mismatch row, while the file is still unsplit.
- It repeats `--input`, `--manifest`, `--repo-root`, `--require-header`, every
  `--exclude`, and `--json` when this run had it. It repeats `--report` only when the
  caller chose a path, and `--file-naming` only when it is `metadata`, because the
  manifest does not record that one. It never repeats `--collision-naming`, because the
  manifest records it. See decision 26.

Standard error carries the same suggestion as one `Next:` line. An argument made only of
letters, digits, and `_ . / : + -` is printed bare. One that also holds a space or a
backslash is put in single quotes, which bash, zsh, and PowerShell read literally. Any
other character, such as `$`, a backtick, `&`, a quote, `=`, `@`, `,`, `%`, or a line break,
turns the line into a sentence that points at `nextArgs`, because no quoting is right for
every shell.
`NextStepWorkflowTests.FollowingNextArgs_RunsTheWholeWorkflow` runs `nextArgs` exactly as printed,
through a real `git mv`, and checks the split is done. See decision 27.

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
- Two inputs never plan one kept-file rename target. The first `split` row that claims a
  target keeps the rename. Each later one keeps its original name, with the note `rename
  collision for <path>; kept original file name`. A skipped row claims nothing. Targets are
  compared the way the platform's disk compares them. The renames phase applies the same
  rule after it has held each row to the manifest, so a file it refuses as `stale plan:`
  claims nothing either. A row whose rename the plan dropped stays dropped, with the same
  note, even when the winner is refused or missing. The content phase reads the kept path
  the manifest holds, so the two files then wait for a new plan.
  `KeptRenameCollisionTests` pins all of it.
- No `split` row plans a new file on a path another input owns. That covers another
  row's rename target, another row's new file, and an input that stays where it is.
  Paths are compared ignoring case. The row with the new file is skipped with
  `planned output path collision with another input requires manual handling`. Its
  `note` reads `output <path> is also claimed by <input>`, once per path and input. In a
  rename against new file conflict, the new file's row is the one skipped. A new file
  is checked against rename targets under its computed simple name, before any
  qualified name is chosen, because the content phase probes that name too. A
  qualified name is checked after it is chosen against every row's original path,
  rename target, and new files. A row that plans a rename still claims its original
  path, because the rename can be dropped later. Two renames to one target follow
  the rule above, not this one. A plan with no such collision is unchanged.
  `RenameTargetCollisionTests` and `QualifiedClaimCollisionTests` pin it. See
  decision 32.
- Every row for a file the run read carries that file's `contentHash`. Verified by
  `ContentHashPlanTests`.
- Every row carries the run's `collisionNaming`: the flag's value, or `file-first` when
  the flag is absent. A plan never reads the value from an existing manifest, because a
  plan starts afresh. Verified by `CollisionNamingFlagTests` and `CollisionNamingRecordedTests`.
- A `skipped` or `failed` row names the file's original path as `keptPath`, with
  `gitMove` false. A refusal never renames. That holds for a `BuildPlan` refusal, an
  empty directive shell, a verification failure, and a collision refusal, even when the
  plan first chose a rename target. Only a `split` row names a planned rename target.
  `RefusedRenameLocationTests` pins it.
- Runs when `--phase` is absent. See decision 25.

You can run `plan` as many times as you like. It is the review step, and it is the reason
a human or an agent can inspect the whole change before a single byte moves.

### renames

Runs `git mv` for files whose name does not match their primary type.

Promises:

- **Never writes the manifest.** It recomputes the plan from the files as they are, and
  writes its rows to the report. Run it with no plan on disk and no manifest appears, so
  `content` still exits 3 until `plan` has run. `--manifest` also anchors the default
  report path here. The path is checked: a linked manifest path is refused, and so is a
  report path that names the same file.
- **A rename onto a directory is a `failed` row.** `git mv` would move the file into a
  directory that sits at the destination, so the phase refuses before it asks git.
- **A rename that fails is a `failed` row.** The run continues, writes the report, and
  exits 1. The row's `keptPath` says where the file is, which is the original path unless
  `git mv` landed before a later step failed. `gitMove` stays true, because the plan still
  asks for the move. The reason starts with `rename to <kept path> failed:`.
- Only renames a file the plan marked `split` with `GitMove` true.
- **Is held to the manifest on disk.** This phase recomputes the plan, so each computed
  row is checked against the manifest row for that file first. Three refusals follow,
  each a `skipped` row with a `stale plan:` reason and no `git mv`. A file this run read
  whose bytes differ from the recorded `contentHash` is refused whatever the recomputed
  row says. A file this run would split that the manifest marks `skipped` or `failed` is
  refused. A file this run would split that the manifest has no row for is refused. Only
  the plan phase starts a new plan.
- A row is found by its original path, or by its kept path once the rename has landed. A
  file at the kept path while the original still exists is not the planned file, even
  with the same bytes, and counts as having no row.
- The check covers the hash and the status, not the whole shape. With matching bytes, this
  phase still reports the new file paths it recomputed. A kept-file rename is the one
  exception: it runs only when the manifest row plans that same rename.
- **Never reports a hash of its own when a manifest exists.** Every row it reports copies
  `contentHash` from the manifest row for that file, whatever the row's status. A file
  the manifest has no row for gets an empty hash. So the report never shows a hash the
  plan phase did not record.
- Throws when the manifest exists and has no `contentHash` column. This ends the whole
  run with exit code 3 before any rename, the same as the content phase.
- With no manifest on disk, nothing was planned and there is nothing to be held to. The
  phase plans and hashes afresh, as the plan phase would, and still writes no manifest.
- Reads each file again just before `git mv` and refuses the rename if those bytes no
  longer match the row's hash.
- `FileSplitter.ApplyRenameOnly` re-reads the moved file and throws if `git mv` changed
  its content.
- Kept as a separate phase so the rename lands in its own commit. Git then records a
  rename instead of a delete plus an add, and the file history survives.

Recomputes the plan. It reads the manifest on disk to hold each file to its recorded hash
and status, to one path, the kept-file rename, and to the plan's `collisionNaming` value.
It never compares a new file path or the `--file-naming` value with the plan, and it never
writes the manifest: its rows go to the report. The collision pass it reruns names
collisions the way the plan did:

- With `--collision-naming` absent, it uses the value the manifest records.
- With a value that matches the manifest, it runs as usual.
- With a value that contradicts the manifest, it ends the run with exit code 3 before any
  rename. The error names both values and says to omit the flag or re-run the plan.
- A manifest with no `collisionNaming` column, or with a blank, unknown, or mixed value,
  also ends the run with exit code 3. See decision 26.
- With no manifest on disk, an absent flag means `file-first`.

Before the column existed, a renames run with a different value computed the other form's
paths. Before decision 23 it also wrote them over the reviewed manifest.

A rename runs only when the manifest row plans that same rename, to that same target. Any
other recomputed rename is dropped and the file stays where the plan left it. That covers a
row with no planned rename and a row whose planned rename has another target. An input
that lost its target to an earlier input in the plan keeps the plan's note, `rename
collision for <path>; kept original file name`. Any other dropped rename gets `rename to
<path> is not in the plan; kept original file name`. A different `--file-naming` can still
leave out a rename the plan asked for. The content phase then fails that row and says to
run the renames phase, and a renames run with the plan's value lands it. A file the plan
skipped under one value that renames would split under the other gets a `stale plan:`
reason, because the manifest does not mark it `split`. Pass the same `--file-naming`
value here as in the plan run, so the report shows the same paths and every planned rename
lands. `--collision-naming` needs no repeat, because the manifest records it.

Requires a real git repository to rename. Most of the test suite avoids this phase by
keeping every fixture file name equal to its primary type name. `ContentHashRenamesTests`
is the exception: its rename tests run `git init` and `git add` in a temporary directory
and then run this phase for real. `RenameFailure_IsAFailedRowInTheReport` asks for a
rename outside a repository to pin the failed row.
`MetadataRenameTests`, `KeptRenameCollisionTests` and two tests in `FileNamingAgreementTests`
also run this phase for real. The first drives a real `git mv` to a file name that contains
a backtick.

### content

Runs only when `--phase content` is given. Reads the plan manifest and performs the split.

Promises:

- **Acts only on rows the plan marked `split`.** Anything else is skipped with the reason
  `not present as split in plan manifest`.
- **A refused row names where the file is.** Its `keptPath` is the path the phase read:
  the renamed path once the rename has landed, and the original path otherwise. It is
  never a rename target this run computed but did not apply. `RefusedRenameLocationTests`
  pins it.
- **Finds a renamed file by its kept path.** A directory scan after the renames phase
  supplies the kept path, not the original. An input that equals the kept path of exactly
  one `split` row with a git move is applied as that row. An original path match wins over
  a kept path match, and a kept path two rows claim matches neither. A row reached through
  both of its paths in one run is applied once. Every rename state and manifest check still
  runs on the row.
- Throws when the plan manifest does not exist. The message names the missing path. This
  one ends the whole run, not just the file.
- Throws when the plan manifest has no `contentHash` column, which means an older version
  wrote it. The message says to re-run the plan phase. This also ends the whole run, with
  exit code 3, before any source file is read. There is no fallback to the structural check
  alone.
- **Takes `--collision-naming` from the manifest.** The rules are the ones renames follows,
  listed above: an absent flag uses the recorded value, and a contradicting flag, a missing
  column, or a blank, unknown, or mixed value ends the run with exit code 3 before any
  source file is read. The `contentHash` check runs first, so a manifest from version 0.1.1
  reports the `contentHash` error, which gives the same advice.
- **Refuses a file whose bytes differ from the planned `contentHash`.** The comparison
  runs straight after the file is read, ahead of parsing and every guard that follows it. The row is `skipped`,
  the reason starts with `stale plan:` and names both hashes, and nothing is written for
  that file. Other files in the run are unaffected. The hash covers raw bytes, so a
  changed body, whitespace, newline style, encoding, or byte order mark all count. A
  renamed file still matches, because `git mv` does not change bytes.
- Refuses a file whose row carries an empty or malformed `contentHash`, with the same
  `stale plan:` prefix. A malformed value is never compared loosely and never crashes the
  run.
- **Never changes a recorded hash.** Every row it writes for a planned file copies the
  planned `contentHash`, including a row for a file it never read: an excluded path, a
  missing file, an inconsistent rename state. The report row for an input the plan did
  not mark `split` repeats the hash of that input's manifest row, when it has one. The
  row is found by the input's original path, or by its kept path while the manifest row
  still records that kept path.
- A renames run over a file an earlier content run already split reports that file as
  `stale plan:`. The content run rewrote the kept file, so its bytes differ from the
  plan. A second content run reports it as `already applied`, as set out below. The file
  is left alone either way.
- Refuses a file when the recomputed plan disagrees with the manifest it was handed. The
  comparison is structural: it covers the kept path, the git move flag, and the set of
  output paths. With the hash in front of it, this check now guards the other side: the
  source is proven unchanged, so a disagreement here means the manifest was edited.
- Refuses a row the plan phase resolved under the other `--collision-naming` value from
  the one this run resolved. Since decision 26 the run takes its value from the manifest,
  so this fires only when someone edited the `collisionNaming` column and left the paths
  and notes alone. Two things must hold: the output path has the other value's qualified
  form, and the row's note records that the collision pass chose that path. The reason is
  the same `content phase plan does not match manifest`, and the skipped row's note names
  the path and both values. The content phase never reruns the collision pass, so this
  check is the last guard between an edited column and names nobody reviewed. A path
  typed into the manifest by hand has no such note and is accepted as before. An edit that
  rewrites the note to name the new path as well is refused, because it then reads as the
  plan's own. The skipped row goes to the report
  and the plan keeps its `split` row. The column was edited, so re-run the plan phase.
- Refuses a row planned under the `--file-naming` value this run was not given. A moved
  type's manifest path is refused when it is the name the other value gives that type,
  alone or in either cross-file qualified form, and that name differs from this run's.
  A changed kept path or rename is caught by the structural comparison above. The reason
  is the same `content phase plan does not match manifest`, and the skipped row's note
  says what differs: the two values and both paths, or the two kept paths. A type that
  is not generic has one name under both values, so its row is never refused this way.
  Any other free path typed into the manifest is accepted as before. The manifest holds
  no record of the value, so typing the other value's exact name by hand is refused too.
  The plan keeps its `split` row, so recovery is to run the content phase again with the
  value the plan was given.
- **Never writes the manifest.** The plan is byte identical before and after a content
  run, whether rows succeed or fail. Results go to the report.
- **Can be retried against the same plan.** A row that failed is still `split` in the
  plan, so the next run applies it once the cause is fixed.
- **Skips a row an earlier run already applied.** Such a file no longer matches the
  planned `contentHash`, so the hash check meets it first. When every planned output
  exists, and the file still declares a type but none of the planned ones, the row is
  `skipped` with the reason `already applied: every planned output exists and this file
  no longer declares the planned types`. Any other changed file is `stale plan:`. The
  check only chooses between two reasons for a skip. Nothing is written either way.
  Verified by `ContentRetryTests`. Callers may match on the leading `already applied`.
- **Reports an interrupted write as stale.** A process killed between the files of one
  split can leave a kept file with no planned type and a planned output missing. A retry
  reports that file as `stale plan:`, not as applied. Check `git status` after any run
  that did not exit normally.

## Guard contracts

These run in order inside `FileSplitter.Process`, and all of them run before any write for
that file.

### What counts as a top level type

A top level type is any class, struct, record, interface, enum, or delegate that is not
declared inside another type. It can sit at the root of the file, or inside a namespace
at any depth. A type in `namespace A { namespace B { class Deep { } } }` is a split
candidate like any other, and lands in exactly one output. The nested namespace block it
sat in stays in every other output, empty, the way a sibling namespace block does.

Two types at different depths can map to the same output file. The plan then skips the
input with `target path collision within split`, and nothing is written. Which pairs
collide depends on `--file-naming`:

| Pair | `stylecop` | `metadata` |
|---|---|---|
| `Foo` and `Foo` | collision | collision |
| `Box<T>` and `Box<U>` | splits, `Box{U}.cs` | collision on ``Box`1.cs`` |
| `Foo` and `Foo<T>` | splits, `Foo{T}.cs` | splits, ``Foo`1.cs`` |

### Input rejection, before parsing

Reason text shown in italics is a template; the run substitutes real values.

| Condition | Result | Reason text |
|---|---|---|
| Path contains an `--exclude` pattern | skipped | `excluded by pattern: <pattern>` |
| File missing | skipped | `input file does not exist` |
| File exists but cannot be read | failed | `cannot read input: <message>` |
| Content phase, bytes differ from the planned hash | skipped | `stale plan: input changed after the plan was written (planned <hash>, found <hash>); re-run --phase plan` |
| Content phase, row has no hash | skipped | `stale plan: the manifest row carries no contentHash; re-run --phase plan` |
| Content phase, row has a malformed hash | skipped | `stale plan: the manifest row carries a malformed contentHash '<value>'; re-run --phase plan` |
| Renames phase, would split a file the manifest does not mark `split` | skipped | `stale plan: the plan manifest does not mark this file as split; re-run --phase plan` |
| Renames phase, would split a file the manifest has no row for | skipped | `stale plan: the plan manifest has no row for this file; re-run --phase plan` |
| Parse produced an error diagnostic, other than an `#error` whose branch names a symbol the build sets (not one the file sets itself outside a symbol-dependent `#if`), and the file still fails with every symbol its `#if` lines name defined | failed | `input has syntax errors: <diagnostics>` |
| Parse fails with no symbols defined, and the retry with its symbols runs past 10 seconds | skipped | `does not parse with no preprocessor symbols, and the retry with its symbols ran past its time limit; manual split required` |
| Parse fails with no symbols defined, and the file's `#if` lines name more than 256 distinct symbols | skipped | `does not parse with no preprocessor symbols, and its #if lines name too many symbols to retry; manual split required` |
| Parse fails with no symbols defined, and succeeds with every symbol the file's `#if` lines name | skipped | `parses only with the preprocessor symbols its #if lines name, which this tool does not define; manual split required` |
| File has a `#:` or `#!` directive (a .NET 10 file-based program) | skipped | `file-based program (#: or #! directive): it compiles alone, so no type can move out of it` |
| File has top level statements | skipped | `contains top-level statements; manual split required` |
| One type or fewer | skipped | `nothing to split: input has <n> top-level type declaration(s)` |
| A `file` type and at most one other type | skipped | `nothing to split: file-local types stay in their file and at most one other type is declared` |
| A `file` type, and text an `#if` excludes anywhere in the file | skipped | `file-local type shares the file with conditionally excluded text; manual split required` |
| A `file` type, and a `global using` directive | skipped | `file-local type shares the file with a global using directive; manual split required` |
| A `file` type, and a `#line`, `#nullable`, `#pragma`, `#define`, or `#undef` directive on or inside a type, or on an assembly attribute | skipped | `file-local type shares the file with a #line, #nullable, #pragma, #define, or #undef directive on or inside a type or on an assembly attribute; manual split required` |
| A `file` type declares an extension method or an extension block | skipped | `file-local type declares extension members; manual split required` |
| Any identifier in a using directive equals a `file` type's name | skipped | `using directive names a file-local type; manual split required` |
| An ordinary type, or a type nested in one, names a `file` type in its base list | skipped | `type lists a file-local type among its base types; manual split required` |
| Anything else that throws while handling one input | failed | the exception message |

The seven `file` rows apply in the order shown, and the first that holds decides the reason.
The last three put the type's name in the row's `note`, for example `Numbers declares
extension members`. The directive row puts the directive and its line there, for example
`#line directive at line 7`. A directive attached to a using directive or to the namespace
keyword belongs to no type, so every output repeats it and this refusal does not apply. One
attached to an assembly attribute is refused, because only the kept file keeps those. A
`#define` or `#undef` attached to a type is refused too. Removing a type keeps it in every
output, but drops an `#if` around it, and the tool does not tell the two cases apart. An eighth `file` reason comes from
`BuildPlan`, described under
[File-local types](#file-local-types).

The `stale plan:` rows are the exception to "before parsing" in one phase. The content
phase applies its three straight after the read. The renames phase computes its plan row
first and then applies all five. There, the missing hash reason applies only to a row
that would split, because a file nobody planned has nothing to be stale against. A
recorded hash that is malformed, or that differs from the bytes, refuses any file the run
read. The
catch all row above them is still last in order.

The "anything else" row is the catch all, and it is deliberate. The content phase rewrites files as it
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
   nothing is. Other files in the run are unaffected. The content hash check described
   under the `content` phase runs before this one and covers edits to the source. The
   comparison of output path sets runs after the manifest paths are substituted in, so it
   cannot see a changed path by itself. Two narrower checks cover the paths a flag decides.
   One refuses a collision name resolved under the other `--collision-naming`. The other
   refuses the name the other `--file-naming` gives a moved type. Any other free path in
   the manifest is applied.

File names come from `--file-naming`. Under `stylecop`, the default, a generic type is
named `Name{T1,T2}.cs` only when another type in the input shares its simple name, and the
kept type is the one whose simple name matches the file. Under `metadata`, every generic
type is named ``Name`N.cs`` with N its arity, the kept type is the one whose arity name
matches the file, and a generic type kept in a plain named file is renamed to its arity
name. The tool never reads `stylecop.json` or `.editorconfig` to choose.

Within a single input file, two types with the same simple name that do not differ by
generic arity are refused with `target path collision within split: <path>`. A type with
the kept type's name and type parameters targets the kept path, so two parts of one
partial type, generic or not, are refused this way even when the kept type keeps the
original file name. The same reason covers two manifest rows that resolve to one path.
Under `metadata`, differing by type parameter names is not enough: `Box<T>` and `Box<U>`
share ``Box`1.cs`` and are refused this way.

### File-local types

A type declared with the `file` modifier never leaves its file, and that file is never
renamed. The type is visible only in that file, and the compiler builds its metadata name
from the file's name. The ordinary types beside it may move out. Decision 30 records the
measurements behind each rule here.

- **No rename, in any phase.** The kept path is the input path and `gitMove` is false. A
  manifest row edited to plan a rename is never applied. When the file was also renamed
  by hand, the content phase skips the row with `content phase plan does not match
  manifest`. When it was not, the row is `failed` with `Content phase expected renamed
  file at <path>`. Ignore that message's advice to run the renames phase: it performs no
  rename for such an input. Re-run the plan.
- **The plan is made over the ordinary types.** The keep rule picks the kept type among
  them. Every other ordinary type moves. The `file` types stay in the kept file.
- **One exception to the keep rule.** When no ordinary type is named for the file, the keep
  rule only takes the first one. If exactly one ordinary type names a `file` type, that
  type is kept instead, because it can never move.
- **A moved type that names a `file` type refuses the input.** The row is `skipped` with
  `moved type names a file-local type; manual split required`, and its `note` names both,
  for example `Caller names AlphaHelper`. Any identifier counts: one in a method body, a
  type argument, an attribute written without its `Attribute` suffix, `nameof`, or a
  `cref` in a documentation comment. The scan compares the identifier's value, so
  `@AlphaHelper` matches. It is a scan by name only. A member that merely shares the name
  refuses the input too.
- **The split row carries a note.** It starts `file-local type stayed in this file:
  <names>`, or `file-local types` for more than one. Every note then says `; SA1402 still flags the file`. That is the
  whole claim when the kept file holds two or more classes or records, which is what SA1402
  checks by default. Otherwise the note scopes it: `; SA1402 still flags the file where it
  checks more than classes`. When the keep rule would have renamed the file, the note adds `; the file
  keeps its name because a rename changes a file-local type's metadata name`. Any
  directive note or collision note follows after ` | `. Callers may match on the leading
  `file-local type`. A content retry reports the row as `already applied` with no note, so
  read the note from the plan manifest.

The split leaves the file with more than one type, so SA1402 can still report it. See
[fix-sa1402-whole-repo.md](fix-sa1402-whole-repo.md#files-that-hold-a-file-type) for how
to suppress that warning.

A `file` type may sit at any namespace depth. It still stays in the kept file, and a type
in a nested namespace is scanned like any other.

Nothing else about the workflow changes. The row carries a content hash, and the content
phase refuses a changed file as `stale plan:`. A retry reports the row as `already
applied`. A moved type takes its name from `--file-naming`, and a colliding one from
`--collision-naming`. Verified by the nine `FileLocal*Tests` classes. Eight of them compile
source with the C# compiler, because a parse cannot show a binding.

### VerifyOutputs

Runs before `WriteOutputs`. Throws, and the file is recorded `failed` and never written,
when any of these hold. The row's reason is the message after the prefix
`output check failed: `, so an agent can tell a refused split from a broken input:

- An output does not start with the required header, when `--require-header` is set.
- An output does not parse.
- A type's declaration plus its owned trivia appears in more than one output, or in none.
  For an input that declares a `file` type the count is not fixed at one: each text is
  expected as often as the input itself holds it, because one declaration's text can sit
  inside another's in the kept file.
- The set of top level types across all outputs does not equal the input set.
- The input declares a `file` type, and no output is written to the path that was read.
  That would be a rename.
- The input declares a `file` type, and an output does not hold the same `#line`,
  `#nullable`, `#pragma`, `#define`, and `#undef` directives as the input, in order. This
  does not see a lost `#if` around a `#define` or `#undef` that stayed. The refusal in
  `FileLocalDirectives.DescribeRefusal` is the only guard for that.
- The input declares a `file` type, and an output for another path declares one or names
  one. `BuildPlan` refuses these inputs first, so this fails a row only after a planning
  mistake.

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

### Blank line collapse

`BuildOutputs` runs each output through `OwnedSpanCollapser`, a `BlankLineCollapser`, before it renders text.

- Outside every type's owned span, a run of line breaks in one trivia list is cut to two.
  A comment's own line break counts as one, so a comment followed by a run keeps one
  blank line below it.
- Every top-level type's owned span is left byte for byte, blank lines included. The span
  is the one `VerifyOutputs` checks, from `OutputVerifier.GetTypeOwnedStart` to the end
  of the declaration.
- The work runs on Roslyn trivia, never on text, so a blank line inside a block comment or
  an `#if` region is never touched.

Verified by `BlankLineRunInsideTypeTests` and `OwnedTriviaTests`.

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
   disk for a qualified name. It does check one against the paths the plan owns: other
   inputs, rename targets, and new files. A hit skips the input with
   `planned output path collision with another input requires manual handling`. The
   content phase probes the disk, and refuses with `target path already exists`. Under
   `type-first`, an input whose qualified name would
   end in `.g`, `.g.i`, `.designer`, or `.generated` is skipped with
   `qualified output name would be read as generated code; manual handling required`.
6. **The tool trusts the plan manifest as a review artifact**, and refuses to proceed when
   reality has drifted from it. That refusal is a feature.
