---
type: context-artifact
layer: philosophical
subject: AgenticRoslynTool split-types
captured: 2026-08-04
captured_by: rjmurillo + AI-assisted analysis
methodology: Context Layer Generator from PromptKit v4 (https://promptkit.natebjones.com/20260402_795_promptkit_1)
sources:
  - src/AgenticRoslynTool/ (every source file; 17 of them at capture time)
  - Original tool, written August 2026 for a closed-source repository
  - Production run in August 2026 that processed 7,818 files and split 461 of them
  - Adversarial review round, August 2026
  - https://lizzy-gallagher.github.io/_site/roslyn-refactoring.html
---

# Decision Log

Why the tool is shaped this way. Each entry ends with a warning describing what breaks if
the decision is reversed. Those warnings are the point of this document. Read the warning
before you "clean up" anything below.

The tool began as a single file utility for one large closed-source repository, where one
run processed 7,818 files and split 461 of them. Repository wide scans before and after
that run counted 13,561 top level types. None of those numbers can be reproduced from
this repository. It was then extracted into this repository as a general-purpose tool.
Several entries below exist because that extraction exposed assumptions the original never
had to question.

## 1. Roslyn Syntax API, with no semantic model

**Decision.** Parse with `CSharpSyntaxTree.ParseText` and work on syntax alone. Do not open
an MSBuild workspace, do not load a solution, do not ask for symbols.

**Context.** The job is mechanical: move a type declaration and the trivia it owns into a
new file. Nothing about that needs type resolution.

**Alternatives considered.** `MSBuildWorkspace` plus `Solution`, the approach most Roslyn
refactoring guides start with, including the article credited above. Rejected because it
requires the target solution to load and compile, which is a hard prerequisite in a large
repository mid refactor, and because it is far slower per file.

**Consequences.** The tool runs on files that do not compile as a set, needs no project
context, and processes thousands of files in one pass. In exchange it can never answer a
question that requires symbols.

> **Warning.** If you add a semantic model to answer one question, you inherit the whole
> MSBuild prerequisite: the target repository must build before the tool can run. That is
> exactly the situation a large mechanical refactor cannot guarantee.

## 2. Using directives are never pruned

**Decision.** Copy every using from the source into every output. Never remove one, even
when it is obviously unused in that output.

**Context.** An unused looking using is not reliably unused. It can supply an extension
method, or a target typed conversion, and telling the difference requires a semantic
model the tool deliberately does not have (see decision 1).

**Alternatives considered.** Prune per output for tidiness. Rejected.

**Consequences.** Outputs carry usings they do not need. An IDE cleanup pass removes them
later, as a separate reviewable change.

> **Warning.** Pruning usings turns this from a mechanical tool into a semantic one. The
> entire promise of the tool is that a reviewer does not have to check for behavior
> changes. One pruned extension method import breaks that promise, and it breaks it
> silently, at compile time in the best case and at runtime in the worst.

## 3. Blank lines are handled through trivia, never through text

**Decision.** `BlankLineCollapser` operates on Roslyn trivia. No regular expression and no
string replacement ever touches the source text to tidy whitespace.

**Context.** A blank line inside a block comment, or inside an `#if` region, is not a
structural blank line. It lives inside a token's trivia text. Text level normalization
cannot tell the two apart.

**Consequences.** The collapser is more code than a regular expression would be, and it is
correct on inputs that would silently corrupt under text processing.

> **Warning.** Replacing this with text processing will quietly rewrite the interior of
> block comments and preprocessor regions. A test covers exactly this: a block comment
> containing blank lines must survive byte for byte.

## 4. Three phases, with a reviewable manifest between them

**Decision.** Split the work into `plan`, `renames`, and `content`. `plan` writes a CSV
manifest and nothing else. `content` refuses to run without that manifest, and refuses to
run when its own recomputed plan disagrees with it.

**Context.** A mechanical change across thousands of files is only safe if a human or an
agent can see the whole change before it happens, and if what gets applied is provably
what was reviewed.

**Consequences.** Three invocations instead of one. A plan whose shape has drifted is
detected rather than applied. The check is structural: it compares the kept path, the git
move flag, and the set of output paths. It does not hash content, so a source edit that
leaves type names and paths alone will not be caught.

> **Warning.** Collapsing this into a single pass removes the review artifact and the
> drift check together. At that point the tool is asking for trust it has not earned, on
> a change too large to review after the fact.

## 5. Renames are a separate phase and a separate commit

**Decision.** Moving a file to match its primary type name happens in its own phase, via
`git mv`, and is meant to be committed before `content` runs.

**Context.** Git infers renames from content similarity. A rename plus a content rewrite in
one commit looks like a delete and an add, and the file's history is lost.

> **Warning.** Merging the rename into the content commit destroys `git log --follow` and
> `git blame` across the refactor. On a change this size that is a permanent loss of the
> history for thousands of files.

## 6. Verification runs to completion before anything is written, per file

**Decision.** `VerifyOutputs` and `VerifyLineConservation` both run before `WriteOutputs`
is called for that file. A file that fails verification is never written and is recorded
`failed`.

**Context.** Partial application of a mechanical refactor is worse than no application,
because it is harder to detect and harder to undo.

**Consequences.** The tool holds one file's outputs in memory before writing them. The
guarantee is per file, not per run: `Run` processes inputs one at a time, so a failure on
the fiftieth input leaves the first forty nine already rewritten on disk. That is what the
plan phase and the manifest are for. `WriteOutputs` also carries a rollback path for the
narrower case where a write itself fails partway.

> **Warning.** Moving any guard after the write reintroduces the partially applied state
> the design exists to prevent. Note also that the rollback code never uses the words
> revert, restore, or rollback, so a keyword search will report that it does not exist. It
> does.

## 7. `OutputFile` carries both `Text` and `BodyText`

**Decision.** Keep the pre-header text alongside the final text, and count line
conservation against `BodyText`.

**Context.** An injected `--require-header` line is by definition absent from the source
file, so it trips the "non prologue line was duplicated" guard. The first fix seeded
header lines into the prologue whitelist. Review showed that whitelisting a line globally
also suppresses the companion "non whitespace line was dropped" check for that same line.

**Alternatives considered.** Whitelisting, as described. Rejected because it weakens a
guard in order to satisfy it.

**Consequences.** A second string on a record, and a guard that is structurally unable to
see injected headers rather than one that has been told to ignore them.

> **Warning.** Counting `Text` instead of `BodyText` lets an injected header mask a
> dropped source line. That is silent data loss in a tool whose entire value is that it
> does not lose data.

## 8. `CsvFieldReader` is hand written

**Decision.** Parse CSV with about 130 lines of hand written code instead of
`Microsoft.VisualBasic.FileIO.TextFieldParser`.

**Context.** The recorded reason was that `TextFieldParser` forces a `net10.0-windows`
target framework. That reason was tested on 2026-08-04 and is false: a plain `net10.0`
console project referencing `Microsoft.VisualBasic.FileIO` compiles and parses quoted
commas correctly with no Windows specific target. The original justification does not
hold, and it is recorded here rather than quietly deleted so nobody re-derives it.

The reader is kept anyway, for weaker but real reasons: it works, it is covered by tests,
it carries no dependency on a Visual Basic compatibility API from a C# tool, and swapping
it out now is a behavior change with no defect driving it.

**Consequences.** The tool is cross platform and CI runs on `ubuntu-latest`. Parity with
`TextFieldParser` is covered by seven tests: simple fields, a quoted comma, doubled quote
escaping, an embedded `\n`, an embedded `\r\n`, multi record ordering, and an empty file.
Seven tests are not a proof of full equivalence.

> **Warning.** Replacing it with a naive `Split(',')` breaks quoted fields and embedded
> newlines, which appear in real manifests. Replacing it with `TextFieldParser` is
> defensible, but verify on Linux first, because the only measurement behind this entry
> was taken on Windows.

## 9. The file header is opt in

**Decision.** `--require-header <text>`, off by default.

**Context.** The original tool hardcoded one company's copyright line and threw when any
output lacked it. In a general purpose tool that means it refuses every file in a
repository that does not use that exact banner.

The original also derived a header by reading the first top level type's leading comment
trivia. That was safe only in the source repository, where the first comment was always
the banner. In the general case it copies the first type's own documentation comment onto
unrelated sibling types, and emits a stray leading blank line. The heuristic was removed
rather than repaired.

> **Warning.** Do not reintroduce header inference from source trivia. A comment above the
> first type belongs to that type. Treating it as a file header misattributes it to every
> other type in the file, and the tool has no way to tell the two cases apart.

## 10. `FileSplitter.cs` is left as one large file

> **Warning.** Superseded by decision 17. The file was decomposed into six types in
> September 2026. This entry stays because the reasoning for deferring it was sound at the
> time, and because the warning at the end of it is still the standing rule.

**Decision.** Over a thousand lines in one type. Not split, despite the tool's own rule
being one type per file, which it does satisfy.

**Context.** The port was mechanical and therefore carried no behavior risk. Restructuring
the engine is a real refactor with real risk, on the one file where a mistake is most
expensive.

**Consequences.** The file exceeds ordinary size and complexity bars. This is a known,
accepted cost.

> **Warning.** This is not an invitation to refactor it casually. Any restructuring needs
> the full test suite green plus an end to end run, because the guards in this file are
> what make every other promise in the tool true.

## 11. No command line parsing framework

**Decision.** Hand rolled argument parsing in `Options.Parse`. One verb, six flags.

**Context.** A dependency would exceed the size of the code it replaces.

> **Warning.** Low severity. If the surface grows past a handful of flags, revisit. Until
> then, a parser dependency is a liability with no matching benefit.

## 12. Types are `internal`, tests use `InternalsVisibleTo`

**Decision.** Nothing in the tool is `public`. Tests reach the internals through
`InternalsVisibleTo` declared in the csproj.

**Context.** This is an executable, not a library. There is no consumer that needs a public
surface, and a public surface is a compatibility commitment.

> **Warning.** Widening a type to `public` to make a test compile creates a permanent API
> obligation for a temporary convenience. Add to the `InternalsVisibleTo` list instead.

## 13. Refusing is the correct outcome

**Decision.** When the tool cannot prove a split is safe, it skips the file and records why.
It never guesses.

**Context.** Top level statements, `file` scoped types, directive regions spanning types,
name collisions, and pre-existing target paths all produce a skip with a specific reason
rather than a best effort attempt.

**Consequences.** A run over a real repository leaves a tail of files for a human. That is
the intended division of labor.

> **Warning.** Turning any of these skips into a heuristic best effort trades a small
> amount of manual work for the possibility of a silent miscompile. The manual tail is
> cheaper.

## 14. Exclusions come from the command line, not from the source

**Decision.** `--exclude <path-substring>`, repeatable, empty by default. Matching is case
insensitive with both separators normalized to `/`, so one pattern works on Windows and
Linux.

**Context.** The original tool hardcoded two lists of paths it refused to touch, taken
from the repository it was written for: `\examples\` combined with `\Ev2\`, `\Templates\`,
`\ServiceModels\` and similar, plus file names ending `ResourceSubjects.cs`, plus anything
under `\docs\samples\`. Those names mean nothing in any other repository, no test covered
them, and they could still fire by accident on a path that happened to match. This is the
same defect as decision 9: one caller's context frozen into a general purpose tool.

**Consequences.** An excluded path produces a `skipped` row naming the pattern that matched
it, rather than vanishing from the manifest the way `\docs\samples\` paths used to. The
exclusion check now runs before the existence check, so an excluded path does not have to
exist.

> **Warning.** Do not reintroduce a built in list. If you find yourself adding a default
> pattern "because everyone excludes it", you are re-creating the defect. The caller knows
> which of their directories are generated. The tool does not.
>
> Decision 16 carves out exactly one exception, and only for directory discovery: `bin` and
> `obj` are not returned by a directory scan. That is a statement about where a build puts
> its output, not about which of the caller's directories are generated, and it does not
> apply to a path the caller listed explicitly. Anything broader belongs in `--exclude`.

## 15. Shipped as a .NET tool package

**Decision.** `PackAsTool` with `ToolCommandName` set to `agentic-roslyn-tool`, published
to NuGet from a `v*` tag.

**Why.** The tool is meant to run against other people's repositories. Cloning and building
this one first is friction that has nothing to do with the job. A tool package supports all
three shapes a consumer might want: `dnx` for a single run with no install, a global install
for repeated use, and a local tool manifest for a team that wants a pinned version checked
in.

**Consequences.** The package name and the command name are now public contract. Renaming
either breaks every script that calls the tool. Packing also pulls the README into the
package, so a broken relative link in the README shows up on the NuGet listing where it
cannot be fixed without a new version. That is why the README links to documentation by
absolute URL rather than relative path.

The workflow does not check that the tagged commit is reachable from `main`, so anyone who
can push a tag can publish a commit that never went through review. Repository write access
is the boundary being trusted here, and it is the same access that could push to `main`
directly. Add an ancestry check if that assumption stops holding.

Publishing authenticates with NuGet trusted publishing rather than a stored API key, so
there is no long lived credential to leak or rotate. The cost is that the nuget.org policy
matches on repository owner, repository name, workflow file name, and environment name.
Those four values are now contract too. Renaming `release.yml`, moving the publish job to
another workflow, or changing `environment: production` silently breaks publishing, and
the failure shows up as a rejected token exchange rather than as anything resembling a
rename error.

Immutable releases are enabled, which makes a version number a one shot resource: once a
release is published, its assets and tag are frozen and that tag name is reserved forever,
even after the release is deleted. Title and notes remain editable. So the workflow never
re-uploads to a published release, it creates a draft and publishes only after the package
is attached, and a re-run of a published version retries the NuGet push alone. `v0.1.0`
was burned by deleting a release to retag it, which is why the first published version is
`v0.1.1`.

## 16. The command line is an agent interface first

**Decision.** `--input` takes a directory, a CSV, a list file, or `-` for standard input.
`--json` prints one report with summary counts. Exit codes distinguish no failures (0), a
failed file (1), a bad command line (2), and a run that could not complete (3). Data goes
to standard output, the summary and every error go to standard error, and no error path
prints a stack trace.

**Why.** The tool is driven by agents, and the first thing an agent did was point `--input`
at a directory. That reached `File.ReadLines` and surfaced "Access to the path is denied",
which sent the caller chasing a permissions problem that did not exist. An unknown flag was
worse: an unhandled `ArgumentException`, a stack trace, and exit code `-532462766`. Both
failures cost turns and taught the caller nothing. Discovery is also the step an agent most
wants delegated, since before this the caller had to already know which files violate
SA1402 and had to write a temp file before it could call the tool at all.

**Consequences.** Directory discovery skips `bin` and `obj`. That is a built-in exclusion,
which decision 14 argues against, so the line matters: those directories are build output
that a build recreates, which is a fact about how .NET lays out a project rather than an
opinion about one repository. Anything narrower stays with `--exclude`. The exit codes and
the JSON field names are now public contract, and the JSON deliberately reuses the CSV
column names and the `split` / `skipped` / `failed` vocabulary so the two formats stay one
contract rather than two that can drift apart. A directory scan cannot reach a file under a
`bin` or `obj` segment at all, and unlike an `--exclude` match it leaves no manifest row
behind, so pass a list file when you need one of those. A directory the scan cannot read
ends the run with exit code 3, because a partial scan exits 0 and is indistinguishable from
a scan that found no work. De-duplication of input paths, and the content phase's keying of
the plan manifest, both follow the platform's filesystem case rules, because a fixed
case-insensitive comparison silently dropped one of `Foo.cs` and `foo.cs` on Linux, where
both are real files. Collision detection stays case-insensitive everywhere on purpose:
there, the conservative answer is the safe one. Usage text moved to standard error on every
failing path, and `--help` and `--version` moved out of `Options.Parse`, which had been
calling `Environment.Exit` from inside argument parsing. Discovery walks the tree by hand
instead of using `EnumerationOptions.AttributesToSkip`, whose reparse-point filter also
applies to files and would have silently dropped a symlinked source file. A `.cs` path is
now the file to split rather than a list of paths, `--dry-run` combined with `--phase` is
rejected instead of letting argument order pick. A plan row the content phase never
received appears in the report as a skip and stays `split` in the plan. An earlier version
rewrote the row as `skipped`, which destroyed the reviewed plan as soon as an agent applied
content in batches. A second version carried the row forward through a `RunOutcome` with
separate manifest and report views. Decision 21 removed that type along with the rewrite it
existed to make safe. A file symlink discovered alongside its target is
de-duplicated by physical identity so one file is never split twice. Case sensitivity is
taken from the operating system, not probed per volume; a case-sensitive volume mounted on
Windows or macOS is a stated limitation rather than a filesystem write on every run.

Automated review of the pull request found three ways this contract still leaked, and all
three are now covered by a test against the real executable. The catch at the process
boundary is unfiltered, because a filtered one only holds until some path throws a type
nobody listed: a manifest with a duplicate header name threw `ArgumentException` out of
`ManifestWriter.Read`, printed a stack trace, and exited `-532462766`, which is the exact
failure this decision exists to prevent. `--help` and `--version` are recognized only in an
option position, because scanning every token turned `--input help` into a successful
no-op: usage on standard output, exit 0, nothing done. That reads as success to a caller
that cannot see the screen. Knowing which tokens are values lives beside the parser switch
in `Options`, since a new value-taking option has to update both or the scan drifts.

The same argument runs one level down, at the per-file boundary. The content phase rewrites
source as it goes and writes its record once at the end, so an exception escaping one
input strands every file already rewritten with no record of them. That record was the
manifest when this was written, and is the report since decision 21. Three review rounds each found another site inside `Process`
that threw past the write: enumeration of a lazy input list, `File.ReadAllBytes` on a file
`File.Exists` had just approved, `GetReadPath` on a rename the operator never applied, and
`BuildPlan` on a manifest carrying two rows for one type. Guarding them one at a time was
losing to the same pattern as a filtered catch, so the guard moved to the boundary:
`Process` wraps `ProcessCore` and turns anything that escapes into a failed row. The cost is
that a genuine bug in the tool now reports as a failed row and exit 1 rather than exit 3.
That is the better trade here, because exit 1 comes with a row naming the input that
failed, and exit 3 came with nothing.

## 17. FileSplitter was decomposed by responsibility, not by pattern

`FileSplitter.cs` was 1442 lines and 49 methods. It read inputs, planned splits, analyzed
preprocessor directives, rendered output text, verified that output, and wrote to the
working tree through `git`. Six responsibilities, one class, coincidental cohesion. Nothing
could exercise plan construction or output verification without a real file system and a
real `git` binary, because every one of those methods was private to a class only drivable
end to end.

Six extractions, one per commit, with the full suite green after each: `InputSource`,
`SplitPlanner`, `DirectiveAnalyzer`, `OutputBuilder`, `OutputVerifier`, `WorkspaceWriter`.
`FileSplitter` kept the orchestration and the per-input pipeline and is now 371 lines.

`WorkspaceWriter` is the one with real leverage. It was the only reason the engine
referenced `System.Diagnostics` and `File.WriteAllBytes`, and that using directive is now
gone from `FileSplitter.cs`. Planning, building, and verifying are reachable without a
repository behind them. `SeamUnitTests` is the proof: five tests driven from a parsed
string with no temporary repository, none of which could be written before.

### What was deliberately not built

**No interface on `WorkspaceWriter`.** There is one implementation, and the suite drives a
real temporary git repository. A real repository checks more than a fake would, so
`IWorkspaceWriter` would exist only to make the design look decoupled.

**No Strategy for the three phases.** The `Phase` enum is branched on in three places, two
of them two lines long. A Strategy would add four types to remove three conditionals,
raising the number of concepts a reader holds. The set of phases is closed, so Open-Closed
has nothing to protect. Recognizing that a pattern does not apply is the pattern-oriented
answer.

**No Strategy for the input shapes.** `ReadInputs` branches five ways at one dispatch point
with no repeated conditional. Five classes to replace one switch is not a trade.

**No behavior changes.** Three known defects live in `FileSplitter.cs`: an unreachable
rollback branch, a non-split row that restates where the file lives, and the unanchored
`EnsureHeader` probe recorded below. Fixing one here would have made every commit in this
series unreviewable as a move. They stay for their own change.

### Decision 18. The three deferred defects, settled

Two were real and are fixed. `EnsureHeader` probed with an unanchored `StartsWith`, so
`// B-extra` satisfied a required `// B`, and it disagreed with `OutputVerifier` about
leading whitespace, which failed any file carrying the banner under a blank line. One
anchored predicate now serves both. The `moved` flag in `WriteOutputs` was never
assigned, and there was nothing to wire it to, so the branch went away and the rollback that
does run gained the test it never had.

The third was recorded as an asymmetric reason field on `FileResult.Split`. That is not a
defect: a success has no reason, which is why the field is null. The real asymmetry sits one
level up. Every refusal site answered with the original path and no git move, correct while
planning and wrong once the renames phase has emptied that path, so the manifest pointed a
reader at a file that had moved. Two things changed. Each site below the resolved read path
now answers with that path, which is by construction where the file is. And `Process`
restores `GitMove` from the planned row, because whether a rename was asked for is a fact
about the plan that cannot go stale. `KeptPath` is deliberately not restored: the planned
value is wrong before the rename lands, and a row naming an empty path sends the next run
looking in the wrong place.

### Decision 19. The content phase finds a renamed file by its kept path

**Context.** A directory input is scanned again in every phase. After the renames phase,
the scan finds `Order2.cs`, not `Stuff.cs`. The content phase looked plan rows up by
original path only. So it skipped `Order2.cs` as not planned, reported the `Stuff.cs` row
as not supplied, and exited 0 with the split undone. A list file captured before the plan
did not hit this, because it keeps naming `Stuff.cs`.

**Decision.** `SplitPlanner.IndexByInputPath` keys each `split` row by its original path,
and also by its kept path when the row has a git move. The content phase then processes
the row exactly as if the original path had been supplied. Three rules keep the lookup from
guessing. An original path match wins over a kept path match. A kept path that two rows
claim maps to neither. A row reached through both of its paths runs once.

**Rejected.** Deriving the content inputs from the manifest instead of from `--input`. That
would also fix the bug, but it changes what `--input` means in one phase, and it breaks
applying a plan in batches.

**Consequences.** No guard moved. `GetReadPath` still demands that the kept path exists
and the original does not, and `BuildPlan` still runs every existence, uniqueness, and
manifest match check. `ManifestPathTamperingTests` is unchanged.

**Reversal warning.** Remove the kept path key and a directory run of all three phases
loses every split whose kept type differs from its file name, at exit code 0.

### Decision 20. Repository rules are tuned for one maintainer and many agents

**Context.** One person maintains this repository, and most changes are written by AI
agents that act with that person's GitHub token. Bots handle dependency updates. Any
review rule that needs a second human either blocks every pull request or gets bypassed.
Any bypass granted to the maintainer is also granted to every agent.

**Decision.** The default branch ruleset requires a pull request, squash merges only,
linear history, the `build` and `CodeQL` checks on an up to date branch,
resolved review threads, and Copilot review. It requires zero approvals and no code owner
review. Neither ruleset has a bypass actor. The Actions token cannot approve pull
requests, and only GitHub owned actions plus an explicit allow list may run. The squash
commit takes its title from the pull request title, so the Conventional Commit title a
reviewer saw is the one that lands on `main`. Renovate and Dependabot both auto-merge
minor and patch updates after the ruleset passes. Renovate waits until a release is at
least 14 days old through `minimumReleaseAge`. Dependabot security updates can merge at
once.

**Rejected.** A bypass for the admin role. It would let the maintainer merge past a red
check, and it would let an agent holding that token do the same. If the rules are ever
wrong in an emergency, turn the ruleset off in the settings page, fix the problem, and
turn it back on. That leaves a record in the ruleset history. Code owner review was also
rejected. With one owner it blocks the maintainer's own pull requests and adds nothing.
CODEOWNERS can still exist so GitHub requests a review automatically. Required signed
commits was tried and removed. GitHub checks every commit on the head branch, not only
the squash commit it signs, so an unsigned local commit by the maintainer or an agent
blocks the merge. Turn it back on only after every machine and agent signs its commits.

**Consequences.** A security fix from Dependabot can merge as soon as checks pass.
This also lets a newly published, compromised security version merge quickly. Merges
made with the Actions token do not trigger `push` workflows. CI does not run again on
`main` after a Dependabot merge. The pull request checks ran on an up to date branch.

**Reversal warning.** Add a bypass actor and every agent can push straight to `main`.
Turn code owner review back on while there is one owner and the maintainer's own pull
requests cannot merge. Removing Renovate's release age lets a freshly published,
possibly compromised version merge itself within minutes.

### Decision 21. Only the plan phase writes the manifest, and runs report to their own file

**Context.** `Program` wrote every run's rows back to the plan path. A content run that
failed a row replaced that row's `split` plan with `failed`. The retry then answered `not
present as split in plan manifest`, and the only recovery was to re-run the plan phase.
The file was both the reviewed plan and the run report, and those two jobs disagree as
soon as a row fails. The maintainer chose the separate report file on 2026-11-19.

**Decision.** The manifest is the plan, and only the `plan` phase writes it. The `renames`
and `content` phases write what they did to a report file in the same CSV format.

- **Where the report goes.** `--report <path>` names it. The default is the manifest path
  with `.report.csv` as its extension, so `split-types.csv` pairs with
  `split-types.report.csv`. A default derived from the manifest name can never be the
  manifest. `--report` naming the manifest is rejected with exit code 2. So is `--report`
  with the `plan` phase, where the flag would be a silent no-op.
- **The report path is proven before the run.** The report is written after every source
  file is rewritten. A path found unusable at that point ends a run that already changed
  files with exit code 3 and no report file. So `ReportFile.Prepare` runs first. It ends
  the run with exit code 3 when the path is the manifest under another name, is itself a
  link or a directory, sits in a directory that cannot take a new file, or names an
  existing file that is not a report. The last rule keeps `--report` off the input list
  and off source files that exist when the run starts. It is the report's answer to rule
  10: the file must be replaceable for a retry, so the check asks what the existing file
  is, not whether one exists.
- **Whether the report path is the manifest is observed, not derived.** The check creates
  a file beside the report path and looks for it in the manifest's directory. Three earlier
  versions resolved symbolic links by hand. One took each link target as final. One
  collapsed `..` as text. One observed the report side and still resolved a linked
  manifest path with a library call that collapses `..` the same way. Each let a report
  path reach the manifest and replace the plan.
- **A linked manifest path is refused in `renames` and `content`.** That removes the last
  path the tool had to resolve. A report path that is a link was already refused. The
  directories either path runs through are followed by the operating system, never by
  the tool.
- **The same question is asked again at the write.** The run creates source files, and
  one of them can land on the report path. `ReportFile.Write` refuses to replace it. The
  rows then go to standard output and the run exits 3, so neither the type nor the record
  is lost.
- **No existing file is opened for writing.** The probe and the report both go to a new
  file beside the report path, and the report is moved into place. A hard link to the
  manifest cannot be seen by any path comparison. The move makes that harmless, because
  it replaces the directory entry and leaves the manifest's bytes alone.
- **Standard output is unchanged when the report is written.** The report is a file. Its
  path goes to standard error on a `Report:` line beside the `Manifest:` line. The one
  exception is a report that cannot be written, covered below.
- **`--json` gains one field.** `report` carries the report path. It is `null` in `plan`,
  and after a report that was not written.
  No existing field changed name or meaning. `manifest` is still the plan path.
- **Renames keeps recomputing.** It still plans from the files as they are, and it no
  longer writes the manifest. Its rows go to the report. A rename has to follow the file
  as it is now, and the content phase already refuses a row whose rename state disagrees
  with the plan, so nothing downstream depended on the rewrite. Decision 19 needs the
  kept path of each `split` row, and the plan phase already records it. A rename that
  fails is now a `failed` row and exit code 1. It used to end the run with exit code 3
  after earlier renames had landed, which left no report to name them.
- **A retry skips what is already done.** A row whose planned outputs all exist, and whose
  file still declares a type but none of the planned ones, is `skipped` with the reason
  `already applied: every planned output exists and this file no longer declares the
  planned types`. The check runs before `BuildPlan` and writes nothing.
- **`RunOutcome` is gone.** It split a run into manifest rows and report rows so a content
  run could rewrite the manifest without losing unapplied rows. With no rewrite there is
  one view, and `FileSplitter.Run` returns it.

**Rejected.** Keeping one file and letting a failed row stay `split`. That restores the
retry, and leaves a file that claims `split` for work that did not happen.

**Rejected.** Having the renames phase replay the manifest instead of recomputing. It is a
defensible design, and it is a second behavior change on a phase this decision did not
need to touch. It can be taken up on its own.

**Rejected.** Reading the previous report to learn which rows were applied. The report is
replaced on every run and a caller may delete it, so it cannot be trusted as state. The
tree can. Nothing in the tool reads a report.

**Rejected.** Treating `target path already exists` as success on a retry. An existing
output proves nothing about who wrote it. The already applied check asks a narrower
question, and a file that fails it still reaches every existence check with no exemption.

**Consequences.** Two files instead of one, and the report is replaced by every run that
completes, so it describes the last such run. A run that ends with exit code 3 before
it starts work writes no report and leaves an earlier one as it was. Removing the earlier
report at the start was tried and reverted: it deleted a file on a run that then aborted
on a mistyped input path. `renames` followed by `content` with no `plan` run now exits
3, because the renames phase no longer leaves a manifest behind. That sequence used to
apply a plan nobody had reviewed. A caller that read results from the manifest path after
`content` now reads the unchanged plan, and must read the report or `--json` instead. A
content run against a manifest an older version already rewrote behaves as before: its
`failed` rows are not `split`, so re-run the plan once.

**Known limits.** A manifest reached through a symbolic link must be passed by its real
path to `renames` and `content`. The `plan` phase does not apply that rule. The report and
manifest paths are compared without regard to case on
every platform, so Linux refuses `plan.csv` beside `PLAN.CSV` although they are two files.
`ReportFile.Prepare` creates a missing report directory and leaves it when the run then
ends early. Another process could create a file at the report path between the check at
the write and the move. The manifest and the report share one format, so the tool cannot tell
them apart. A second plan manifest passed as `--report` is replaced when the run
completes. The already
applied check does not compare the types that remain with the plan, so a source edited
after planning can read as applied when its planned outputs exist. A process killed
between the files of one split can leave a planned output missing while the kept file no
longer declares its type. A retry reports that file as `nothing to split`. That window
predates this decision, and the retry contract makes it worth closing in its own change.

**Reversal warning.** Write a later phase's rows back to the manifest and a failed row
erases its own plan again. The retry then reports the file as not planned, at exit code 0.
Remove the already applied check and a retry still exits 0, but every applied row reads
`nothing to split`, which a caller cannot tell from a file that never needed splitting.
Widen that check into an exemption from the existence checks and an edited manifest can
aim a write at an existing file, which rule 10 forbids. Open the report path directly,
to probe it or to write it, and a report path that is a hard link to the manifest empties
the plan. Drop the check at the write and a report can replace a source file the run just
created.

## Credit

The approach owes a debt to Lizzy Gallagher's write up on Roslyn refactoring,
<https://lizzy-gallagher.github.io/_site/roslyn-refactoring.html>. The main departure is
decision 1: that article uses a workspace and a semantic model, and this tool deliberately
does not.
