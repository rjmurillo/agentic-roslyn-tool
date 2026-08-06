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
| Idempotency | Yes for `plan`. Yes for `content` once a file is already split, because a single type file is skipped. |
| Side effects | `plan` writes only the manifest. `renames` runs `git mv`. `content` writes source files. |
| Failure mode | Per file. An unexpected exception while reading, parsing, planning, or writing becomes a `failed` row and the run continues. So does a verification failure. Only a throw before the read path is resolved, or a missing content manifest, ends the run. |
| Exit code | 0 no failures, 1 at least one row `failed`, 2 the command line was wrong, 3 the run could not complete. Skips are not failures. |
| Streams | Standard output carries a parseable document or nothing: the manifest CSV in `plan`, the JSON report under `--json`, and nothing in `renames` and `content` without `--json`. The manifest path, the one-line summary, and every error go to standard error. |
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
stack trace, and no error path writes to standard output.

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

A plan row the content phase's input never supplied stays in the rewritten manifest
verbatim, still `split` and still actionable, and appears in the run report as `skipped`
with the reason `planned as split but not supplied to the content phase input`. The
content phase overwrites the manifest it read, so applying a plan in batches would
otherwise destroy the reviewed plan for every batch after the first.

The mirror case behaves the opposite way. An input the plan never covered is reported as
`skipped` with the reason `not present as split in plan manifest`, and is left out of the
manifest. The manifest is the plan of record and must not grow a tail of rows the plan
phase never produced. So `summary.total` counts what this run reported, which in the
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
whether work happened. `total` counts manifest rows, which is one per distinct input path.

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

- Only touches rows the plan marked `split` with `GitMove` true.
- `FileSplitter.ApplyRenameOnly` re-reads the moved file and throws if `git mv` changed
  its content.
- Kept as a separate phase so the rename lands in its own commit. Git then records a
  rename instead of a delete plus an add, and the file history survives.

Requires a real git repository. The test suite avoids this phase by keeping every fixture
file name equal to its primary type name, so no `git` process ever starts.

### content

The default. Reads the plan manifest and performs the split.

Promises:

- **Acts only on rows the plan marked `split`.** Anything else is skipped with the reason
  `not present as split in plan manifest`.
- Throws when the plan manifest does not exist. The message names the missing path. This
  one ends the whole run, not just the file.
- Refuses a file when the recomputed plan disagrees with the manifest it was handed. The
  comparison is structural: it covers the kept path, the git move flag, and the set of
  output paths. The manifest carries no content hash, so an edit that leaves type names
  and paths alone passes this check. Treat it as drift detection on the plan shape, not as
  proof the source is unchanged.
- Writes its own manifest to the same default path, which **overwrites the plan
  manifest**. Pass an explicit `--manifest` when you want to keep both.

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
goes and writes the manifest once, at the end, so an exception that escapes one input strands
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
generic arity are refused with `target path collision within split: <path>`. The same
reason covers two manifest rows that resolve to one path.

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
4. **The content phase can overwrite the plan manifest.** Same default path.
5. **A cross file name collision is usually resolved to `FileName.TypeName.cs`**, which
   satisfies SA1402 but violates SA1649, so those files need a human decision afterwards.
   When the qualified names collide too, the tool gives up and skips every input involved
   with `qualified output path collision requires manual handling`.
6. **The tool trusts the plan manifest as a review artifact**, and refuses to proceed when
   reality has drifted from it. That refusal is a feature.
