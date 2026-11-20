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
| `src/AgenticRoslynTool/` | The entire product. 40 files. |
| `tests/AgenticRoslynTool.Tests/` | xUnit tests. 336 of them. |
| `docs/behavior-contracts.md` | What each phase and each guard promises. The semantic layer. |
| `docs/decision-log.md` | Why the tool is shaped this way, and what breaks if you undo it. |
| `assets/icon.png` | The NuGet package icon. Packed at the package root by the csproj. |
| `package/AGENTS.md` | Agent instructions packed at the NuGet package root. NuGet MCP returns this file. |
| `package/skills/split-types/SKILL.md` | The Agent Skill packed at `skills/split-types/SKILL.md`. Skill tools such as `dotnet-inspect` read this file. |
| `docs/agent-skills-in-nuget.md` | What was tested about skills in a tool package, what finds them, and how to check again. Read before touching `package/`. |
| `.github/workflows/ci.yml` | The CI gate. Build, test, pack, and check the package's entry names on push and pull request. |
| `.github/scripts/verify-package-contents.sh` | Fails when the `.nupkg` lacks an agent file, by exact entry name. CI and the release workflow both run it. |
| `.qualityrc.json` | One waiver for a local review scanner some agents run. Not read by the build or by CI. |

Every source file, so you do not have to open one to find out whether it matters:

| File | Responsibility |
|---|---|
| `FileSplitter.cs` | The orchestrator. Runs a phase over the inputs and turns one input into one result row. Delegates the actual work to the six types below it, and the plan binding to `PlanBinding.cs`. Read this first. |
| `InputSource.cs` | Turns whatever the caller supplied, a directory, a list file, a manifest, standard input, into an ordered list of absolute paths. |
| `SplitPlanner.cs` | Decides what a split would do: which type stays, where each moved type lands, which inputs collide, which path a phase reads. Does not act. |
| `DirectiveAnalyzer.cs` | Decides whether preprocessor directives make a split unsafe. Returns a verdict, never acts on one. |
| `OutputBuilder.cs` | Renders the text of every output file: name, carved source, required header, trailing newline. |
| `OutputVerifier.cs` | The correctness checks, run against in-memory outputs before anything is written. A failure here means nothing reaches disk. |
| `WorkspaceWriter.cs` | The only place the tool touches the working tree. Every rename runs through `git mv`; every write preserves encoding and byte order mark. |
| `GitCommand.cs` | Runs one git command with a time limit. Passes each argument on its own, reads both streams, and kills git and its children on expiry. |
| `Program.cs` | Top level statements. Verb dispatch, error handling, exit codes, output selection. Decides which file a run writes: the manifest in `plan`, the report otherwise. Declares no type. |
| `Options.cs` | Command line parsing and the option record. Add a new flag here. |
| `RunReport.cs` | Builds the `--json` report and the one-line summary from the rows a run returned. Carries `collisionNaming` and `nextArgs` at the top level. |
| `NextStep.cs` | Decides `nextArgs`, the arguments for the next phase, and the `Next:` line on standard error. Read decision 27 before changing when it returns null. |
| `ReportFile.cs` | Owns the report file. Refuses an unsafe report path before a renames or content run changes anything, then writes the report by moving a staged file into place. Never opens an existing file for writing. |
| `RunSummary.cs` | The aggregate counts inside that report. |
| `FileReport.cs` | One manifest or report row in JSON form. Field names mirror the CSV columns. |
| `NewFileReport.cs` | One created file inside a `FileReport`, carrying its path and type name. |
| `PathComparison.cs` | The path equality rule used for identity. Platform aware on purpose. |
| `Phase.cs` | The `plan`, `renames`, `content` enum. |
| `CollisionNaming.cs` | The `file-first`, `type-first` enum behind `--collision-naming`. |
| `CollisionNames.cs` | Builds the qualified name for a cross file collision, and recognizes one the plan phase wrote. |
| `CollisionNamingFlag.cs` | The `file-first` and `type-first` spellings, and `Resolve`, which picks a run's naming from the flag and the manifest. |
| `FileNamingConvention.cs` | The `stylecop`, `metadata` enum behind `--file-naming`. Selects the naming rule `OutputBuilder` and `SplitPlanner` apply. |
| `PlanMismatch.cs` | Words the note for a content phase row that disagrees with its manifest: the other `--file-naming` name, or what differs in the plan shape. |
| `KeptRenameCollisions.cs` | Decides which planned kept file renames stand. Gives a rename target to the first input that claims it, and holds the renames phase to the renames the manifest plans. |
| `EncodedSource.cs` | Reads bytes into text while capturing encoding, byte order mark, and newline style, then writes them back unchanged. |
| `TopLevelType.cs` | One top level type declaration plus the file name it maps to. |
| `SplitPlan.cs` | The decision for one input: keep, rename, split, or refuse. |
| `PlannedFile.cs` | One output file inside a plan, before content is rendered. |
| `PlannedOutputEntry.cs` | A planned output path paired with its owning input, used for cross file collision detection. |
| `OutputFile.cs` | Rendered output. `BodyText` excludes any injected header; `Text` includes it. |
| `BlankLineCollapser.cs` | Collapses the blank line runs left behind when a type is removed. |
| `DirectiveInfo.cs` | One preprocessor directive and its span. |
| `DirectiveSafety.cs` | Verdict on whether directives make a file unsafe to split. |
| `FileResult.cs` | One manifest or report row. Owns the `split`, `skipped`, `failed` status vocabulary. |
| `NewFileResult.cs` | One created file inside a manifest or report row. |
| `ManifestWriter.cs` | Reads and writes the CSV format shared by the plan manifest and the run report. |
| `ManifestCollisionNaming.cs` | The `collisionNaming` column in the manifest and the report. `Read` refuses a missing, blank, unknown, or mixed value. |
| `ContentHash.cs` | Computes and compares the `sha256:` content hash a manifest row carries for its input. Owns the `stale plan:` refusal text. |
| `PlanBinding.cs` | Binds the rows renames and content report to the plan manifest they read. Carries the planned hash and refuses a stale file. Decides, never acts. |
| `CsvFieldReader.cs` | Quote aware CSV field parsing for the manifest and for CSV inputs. |

The tool splits itself cleanly. Running it over `src/AgenticRoslynTool/` reports every file
skipped and exits 0. That is the cheapest smoke test in the repo, and it is how you confirm
an end to end change actually works.

## Build, test, and verify

```powershell
dotnet build -c Release      # must be 0 warnings, 0 errors
dotnet test  -c Release      # must be 336 passed, 0 failed
```

`TreatWarningsAsErrors` is on, so a warning is a build break. `EnforceCodeStyleInBuild`
is on as well. There is no separate lint step to run.

The SDK is pinned in `global.json` to 10.0.403 with `rollForward: latestMinor`. The
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

12. **A run that starts a plan records content hashes. Every later phase only carries and
    compares them.** Every manifest row for a file the plan read carries the SHA-256 of
    the input's raw bytes. The content phase refuses a file whose bytes differ. The renames phase renames only a
    file the manifest marks `split` whose bytes match. A manifest with no `contentHash`
    column ends a renames or content run with exit 3. Never add a fallback to the
    structural check, never hash decoded text, and never let renames or content report a
    hash it computed: each row copies the hash of the manifest row for that file, on every
    status and every exit. Review found a launder path through each exit that forgot
    this. The `ContentHash*Tests` classes pin all of it. See decision 22.

13. **A run with no `--phase` plans.** Only `--phase content` rewrites source. An agent's
    first call usually has no phase, so that call must change nothing. See decision 25.

14. **The plan records `--collision-naming`. Later phases read it and never default it.**
    `ManifestWriter.Write` takes the value as a required argument. `Options.CollisionNaming`
    is null when the flag is absent, and null is not `file-first` once a manifest exists. A
    contradicting flag, a missing column, and a blank, unknown, or mixed value each exit 3
    before any file is touched. See decision 26.

15. **Every command in a packaged agent document must parse.** `package/AGENTS.md` and
    `package/skills/split-types/SKILL.md` both carry the workflow on purpose: NuGet MCP
    reads the first, skill tools read the second. `PackagedAgentDocsTests` runs each full
    `split-types` command and each backticked flag fragment through `Options.Parse`. It
    proves the commands still parse, not that the two files agree in prose. Rename or
    remove a flag and update both files in the same change. See decision 28.

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
- **Only the plan phase writes the manifest.** `renames` and `content` write the report
  file, by default the manifest path with `.report.csv` as its extension. A test that calls
  `FileSplitter` directly cannot see this, because the choice of file lives in
  `Program.cs`. Test it through the real executable, as `SeparateRunReportTests` does.
- **A content retry meets rows that already succeeded.** Their bytes no longer match the
  plan's hash, so the hash check meets them first. It asks `SplitPlanner.IsAlreadyApplied`
  and skips them as `already applied`. Anything else that changed is `stale plan:`. The
  check only ever picks the reason on a skip. Do not turn it into an exemption from the
  hash check or from the existence checks in rule 10. `ContentRetryTests` pins the states
  that only look applied. Callers match on the words `already applied`, so do not reword
  the start of that reason.
- **The report path is checked before the run, and again at the write.** `ReportFile.Prepare`
  runs before `FileSplitter.Run`. Move it after and an unusable `--report` path is found
  only once every source file is rewritten. `ReportFile.Write` asks again, because the run
  may have created a source file at that path. Drop the second check and `--report
  src/Beta.cs` replaces a type the run just split out, at exit code 0.
- **Do not work out by hand whether two paths name one file.** Three versions of
  `ReportFile` resolved symbolic links themselves. One took each link target as final, one
  collapsed `..` as text, and one called `File.ResolveLinkTarget`, whose `FullName` also
  collapses `..` as text. Each let `--report` reach the manifest and replace the plan.
  `ReportFile` now resolves nothing. It refuses a report path or a manifest path that is
  itself a link, and `ReachesManifest` observes the rest: a file created beside the report
  path is visible under the manifest's directory exactly when the two are one directory.
- **A path read as text is not the path the system opens.** `plan.csv/` opens the file
  `plan.csv`, and `Path.GetFileName` returns an empty name for it. A check that compared
  names missed that the report and the manifest were one file. Reject a trailing
  separator before any code compares file names.
- **`File.Exists` cannot tell absent from unknown.** It answers false when the look
  fails, for example on a path past the system limit. `ReportFile.Look` keeps the two
  apart, and every caller treats a failed look as the unsafe case. A guard built on
  `File.Exists` fails open where it protects a file, and one such guard let the report
  replace the plan.
- **Nothing in `ReportFile` opens an existing file for writing.** The probe and the report
  both go to a new sibling file. An earlier version probed with `FileMode.Create` on the
  report path. That truncated a plan reached through a hard link, at exit code 0.
- **A test that edits a file between plan and content now gets `stale plan:`, not the
  reason you were aiming for.** The hash check runs first. To reach a later guard, such as
  the structural mismatch, leave the source alone and edit the manifest row instead.
- **A renames test with a manifest on disk is now held to it.** A file the manifest does
  not mark `split`, or has no row for, is refused. Delete the manifest, or plan first, if
  the test wants renames to plan afresh.
- **The renames phase needs a tracked file, not a commit.** `git init` and `git add` are
  enough for `git mv`, so a test needs no author identity on the machine. See the helper
  in `ContentHashTestBase`.
- **A local size scanner gates new test files, and one of its scores is waived for tests.**
  One review toolkit scores files by line counts. Split a test class before it fails
  that gate: the content hash tests are eight classes for that reason, seven of them over
  `ContentHashTestBase`, and each passes the cohesion and coupling scores as written. The repeated line
  score is the exception. It counts `[Fact]`, `{`, and `}` as duplicates, so a class of
  four short tests with no repeated statement scores 7.8 against a floor of 8.
  `.qualityrc.json` lowers that one floor to 6.5 for test files only. Every other value in
  it restates a scanner default, including the scanner's own floor of 3 for test
  testability. Do not lower another floor to get a file through.
- **Check that scanner's version before you act on its scores.** On 2026-11-20 the
  installed plugin copy (`b5e05e9c6ba4`) lagged upstream in two ways. It counted a
  `using var x = ...;` statement as an import, so a test that creates four disposables
  failed coupling (upstream issue #7459). It counted braces and doc comment lines as
  duplicates, which upstream fixed in #7473. Both fail new test files here with no real
  defect. Upstream `main` at `99bff810ea`, run from a venv with `tree-sitter==0.26.1`
  and `tree-sitter-c-sharp==0.23.5`, passed this repository's last branch in regression
  mode. The gate also stops at the first failing quality, so one run names one file
  (upstream issue #7481). Read the JSON for every file instead of trusting the log.
- **`--collision-naming` lives in the manifest now, and there are two guards.** The plan
  records it in the `collisionNaming` column. Renames and content resolve it once, through
  `CollisionNamingFlag.Resolve`, and a flag that contradicts the column exits 3 before any file
  is touched. That is the first guard, and `CollisionNamingRecordedTests` pins it through
  the real executable. The second is older: the content phase never reruns the collision
  pass, so the per-row check in `SplitPlanner.BuildPlan` refuses a row the plan resolved
  under the other value. Since the column, only a hand edit of the column reaches it, so
  `CollisionNamingContentTests` writes the manifest with the other value to exercise it.
  The check reads the row's note, so it depends on `CollisionNames.FormatResolution`.
- **Tests that write a manifest must pick a collision naming.** `ManifestWriter.Write` takes
  it as a required argument on purpose. Pass the value the plan used, or the test reaches
  the contradiction error instead of the behavior it means to check. Use
  `CollisionNamingFixture.PlanAndRecord` to plan and write the way `Program` does.
- **Exit codes are 0, 1, 2, 3, and they mean different things.** 0 the run completed with
  no failures, 1 a row is `failed`, 2 the command line was wrong, 3 the run could not
  complete. Skips are not failures, so a run that skips everything exits 0. Read
  `summary` from `--json`, or the summary line on standard error, to learn whether work
  happened.
- **Standard output is one document or nothing, standard error is everything else.** The
  manifest path, the report path, the summary line, the `Next:` line, and every `error:`
  message go to standard error so a caller can pipe standard output straight into a
  parser. Do not add chatter to standard output. When the report file cannot be written,
  its rows print to standard output as that one document, and the run exits 3.
- **`--file-naming` must be the same in every phase.** It decides planned paths. The
  content phase skips a file whose manifest row was made under the other value.
  `SplitPlanner` refuses a moved type whose manifest path is the name the other value gives
  it, and the shape check catches a changed kept path. The skipped row's note says which,
  in words from `PlanMismatch`. Any other hand-edited free path is still applied, which
  `CollisionNamingContentTests` pins, so do not widen the check. The plan keeps its `split`
  row, so run the content phase again with the plan's value. The renames phase runs only
  a rename the manifest row plans, to the planned target, which
  `KeptRenameCollisions.HoldToPlannedRename` enforces. Under another value it can still
  leave out a planned rename. See decision 24 and the known issue below. `nextArgs`
  repeats `--file-naming metadata` for the next phase, because the manifest does not
  record it; see decision 27.
- **A backtick in a path is escaped in `--json` output.** ``Foo`1.cs`` is written as
  `Foo\u00601.cs`. Parse the JSON before you search it for a file name. The CSV manifest
  holds the character itself.
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
- **A burned tag name reports itself as a ruleset violation.** Pushing or creating a tag
  whose name a release already used fails with `Cannot create ref due to creations being
  restricted`, which reads like a branch or tag protection rule and is not one. Ordinary
  tag pushes work fine. The giveaway is the second line, `tag_name was used by an
  immutable release`. Do not go hunting through rulesets for this.
- **A draft release does not create its tag.** GitHub files it under a placeholder
  `untagged-` name and creates the tag only when the draft is published. That is what
  makes the workflow's draft, upload, publish order safe to retry, and it was verified
  against this repository rather than assumed.
- **Two dependency bots, split on purpose.** Renovate opens version updates, and only
  for releases at least 14 days old. Dependabot opens security updates only, at once.
  `open-pull-requests-limit: 0` in `.github/dependabot.yml` is what keeps Dependabot off
  version updates. Renovate's `vulnerabilityAlerts` is off so it does not repeat
  Dependabot. Undo either line and both bots open the same pull request. Both bots merge
  minor and patch updates themselves once the ruleset passes. Majors wait for a human.
  Dependabot covers NuGet only. GitHub raises no security alerts for SHA-pinned actions,
  so a vulnerable action is only fixed by Renovate, after the 14 day wait.
- **Dependabot security fixes merge after the ruleset passes.** The workflow reads
  versions from the `Directory.Packages.props` diff and refuses major bumps. Renovate's
  14 day release age applies only to Renovate updates. A daily schedule retries open
  Dependabot pull requests if the event run did not enable auto-merge.
- **No ruleset has a bypass actor, on purpose.** Agents here run with the maintainer's
  token, so anything the maintainer can bypass, an agent can bypass too. Every change to
  `main` goes through a pull request and the required checks. Code owner review is off
  because the only owner cannot approve their own pull request. See decision 20.
- **Every action must be pinned to a full commit SHA.** The repository's Actions policy
  enforces it, so a workflow that uses a tag such as `@v4` fails before any step runs.
  Pin the SHA and put the version in a trailing comment. Only actions owned by GitHub,
  plus `NuGet/login` and `dependabot/fetch-metadata`, are allowed to run. A new third
  party action fails until the maintainer adds it to the allow list in the Actions
  settings.
- **A `v*` tag cannot be moved or deleted.** A tag ruleset blocks both. Creating one is
  allowed, because the release workflow creates the tag when it publishes.
- **The `production` environment waits for the maintainer.** A release job pauses until
  the maintainer approves it, and only `main` and `v*` tags can deploy there. A published
  version can never be reused, so that click is the last human check. An agent must never
  approve a pending deployment, even though the maintainer's token would let it.

- **`PackagePath="skills"` is the only value that packs the skill correctly.** With a
  `**` wildcard, NuGet appends the matched folder itself. `skills\%(RecursiveDir)` nested
  the file one level too deep, and `skills\` wrote the entry as `skills//split-types/SKILL.md`.
  A substring check accepts the second one. `verify-package-contents.sh` matches exact
  names for that reason.
- **A tool package can never be a `PackageReference`.** Restore fails with `NU1212`. So
  anything that finds skills by scanning a project's packages, such as
  `dotnet-inspect project` or `project.assets.json`, never sees this package's skill. Use
  `dotnet-inspect package AgenticRoslynTool -S Skills`, or the install path in
  `docs/agent-skills-in-nuget.md`.
- **An old `dotnet-inspect` shows no skills, for any package.** Version 0.5.3 lists none,
  even for `markout`, the package the NuGet team uses as its example. 0.28.0 lists them.
  Run `dnx dotnet-inspect@0.28.0 --yes -- ...`, the version tested here, to get a current one before concluding a skill is
  missing.
- **A test cannot take an internal type as a theory parameter.** xUnit test methods are
  public, and rule 7 keeps `Phase`, `FileResult`, and the rest internal. Pass a case name
  as a string and map it inside the test, as `NextStepTests` does. Never widen the type.
- **Most tests that start a child process share one limit.** `CliRunner.Run` and `RunIn`
  start the real executable, and `GitFixture` starts `git`. Both read the redirected
  streams at the same time and wait at most two minutes through `CliRunner.WaitOrKill`,
  which kills the process tree and fails the test. A hung child must fail a test, not
  stall a CI or release job. Add new process tests through these helpers. One exception
  remains: `FileNamingRuns` (from #57) has its own runner with a 60 second limit. A
  renames test that runs `FileSplitter` in process reaches `WorkspaceWriter`'s `git mv`,
  which has its own 120 second limit in the product, set in `WorkspaceWriter`. The CI `build` job and the release
  `publish` job each set `timeout-minutes: 15`.
- **`GitCommand` passes each git argument on its own.** An older version joined the
  `git mv` paths into one quoted string, so a double quote in a file name broke the
  command. Keep `ArgumentList`. `WorkspaceWriterGitTests` pins both this and the time
  limit. Its hang test replaces `.git/index` with a named pipe, which makes a real git
  block, so it runs on Unix only. It checks that git is gone after the kill by reading
  `/proc`, so that one check runs on Linux only.
- **`nextArgs` being null does not mean the split is done.** The skill decides "done" with
  an allow list: a `content` report, no failed row, and every skipped row's reason either
  `not present as split in plan manifest` or starting with `already applied`. Three review
  rounds each found one more reason an exclusion list missed. Do not turn the rule back
  into a list of bad reasons. `SkillDoneRule` mirrors it in tests, and
  `PackagedAgentDocsTests` fails if the docs stop quoting `FileSplitter.NotPlannedReason`.
- **`nextArgs` is null for `--input -` on purpose.** A second process reads an empty
  stream, plans nothing, and exits 0. A test that pipes paths and expects `nextArgs` is
  testing the wrong thing.

## Known issues, not yet fixed

Recorded so nobody rediscovers them and nobody assumes they are intentional.

- The header probe matches a multi-line banner literally, so an existing banner whose
  internal lines carry trailing spaces or tabs is not recognized and a second copy is
  injected. Trailing whitespace on the final banner line is tolerated. Matching every line
  loosely would need a line-by-line comparer, which is not worth it until a real file hits
  this.

- The renames phase holds each file to the manifest's hash and status, and runs a rename
  only when the manifest row plans that same rename. It does not check new file paths or
  `--file-naming`. `--collision-naming` is different: the plan records it, and renames
  exits 3 on a contradicting value (rule 14). A renames run with a different `--file-naming` moves nothing the plan
  did not ask for, but it can leave out a rename the plan did ask for, and it reports that
  row as `split`. The content phase then fails the row with `Run --phase renames`, and a
  renames run with the plan's value recovers.
  `RenamesPhase_UnderAnotherConvention_CanLeaveOutAPlannedRename` pins this. A fix means
  the manifest recording `--file-naming` too, as decision 26 does for `--collision-naming`.
  That waits on a decision. See decisions 24 and 26.
- A kept file's rename target is checked against other kept renames, not against new
  files. `KeptRenameCollisions` gives a rename target to the first input that claims it.
  `ResolvePlannedOutputCollisions` looks at new files only. So if one input plans to rename
  its kept file to a name another input plans as a new file, the plan shows both as
  `split`. After the rename lands, the content phase skips the second input with
  `target path already exists`. Nothing is overwritten. `--file-naming metadata` makes this
  easier to reach, because a generic type kept in a plain named file is renamed.
- A renames run over a file the content phase already split reports it as `stale plan:`.
  The content run rewrote the kept file, so its bytes differ from the plan. The content
  phase tells its own earlier write from an outside edit and says `already applied`. The
  renames phase does not make that check. The file is left alone either way.
- `CsvFieldReader` exists, but not for the reason previously recorded here. The claim was
  that `Microsoft.VisualBasic.FileIO.TextFieldParser` forces a Windows only target. That
  was tested on this machine and is false: it compiles and parses quoted commas correctly
  on plain `net10.0`. The custom reader is kept because it works, is covered by seven
  tests, and carries no dependency on a Visual Basic compatibility API. Replacing it with
  `TextFieldParser` is a legitimate simplification nobody has measured on Linux.

## Where to record what you learn

If you discover something a future agent needs, put it in the repository, not in a chat
reply or a pull request body. Neither survives.

| Kind of knowledge | Where it goes |
|---|---|
| A trap, an invariant, or a build rule | This file |
| What a phase or a guard promises | `docs/behavior-contracts.md` |
| Why a design choice was made, and what breaks if reversed | `docs/decision-log.md` |
| What a specific type or method does | An XML doc comment on that member |
