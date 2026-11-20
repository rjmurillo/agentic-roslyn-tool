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
detected rather than applied. That check is structural: it compares the kept path, the git
move flag, and the set of output paths. On its own it missed a source edit that left type
names and paths alone. Decision 22 added a content hash to close that gap.

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

Decision 30 amends this one for `file` scoped types. The ordinary types beside one can now
move, under a proof that decision records. The rule above did not change.

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
separate manifest and report views. Decision 23 removed that type along with the rewrite it
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
manifest when this was written, and is the report since decision 23. Three review rounds each found another site inside `Process`
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

### Decision 21. Collision naming is a flag, and the default keeps the SA1649 violation

**Superseded in part by decision 26.** The plan now records the value in the manifest.
The rejection of a column below, and the consequences of a forgotten flag, describe the
behavior before that change. The note dependency and the per-row content check still hold.

**Context.** Two inputs in one folder can each emit `Shared.cs`. The collision pass
qualifies both as `<OriginalFileName>.<TypeName>.cs`, such as `A.Shared.cs`. SA1649 reads
a file name up to its first dot, so it reads `A` and reports the file. The naming guide
told readers to rename by hand to `Shared.A.cs`. That is mechanical work the tool can do.
The StyleCop source confirms the rule: `FileNameHelpers.GetFileNameAndSuffix` cuts the
name at the first dot, read at commit `4502632`. A build with StyleCop.Analyzers
`1.2.0-beta.556` on .NET SDK 10.0.404 confirmed the result.

**Decision.** `--collision-naming file-first|type-first` selects the qualified form.
`file-first` is the default and is today's behavior. `type-first` emits
`<TypeName>.<OriginalFileName>.cs`. Both forms are built from the target file name the
plan already chose, so a generic keeps its braces: `Shared{T}.A.cs`. The content phase
refuses a row the plan resolved under the other value, with
`content phase plan does not match manifest`. It knows such a row by two facts together:
the path has the other value's qualified form, and the row's note records that the
collision pass chose it.

**Rejected.** Changing the default to `type-first`. Every plan with a collision would get
new paths, and a manifest reviewed before the upgrade would stop matching. Reading
`stylecop.json` or `.editorconfig` to choose was also rejected. That is the defect in
decisions 9 and 14: the tool guessing at one repository's setup. Recording the value in
the manifest was rejected too. A column would give exact detection and one refusal before
any row is touched. It also changes the manifest CSV for every caller, to serve one flag.
Revisit it if a second flag ever changes planned paths.

**Consequences.** The value must be passed to every phase. The renames phase reads the
manifest only for each row's hash and status, never for its paths, so it does not check
the value. See decision 22. When this was written it also wrote the manifest again from
its own plan, so a renames run with a different value replaced the reviewed collision
names. Decision 23 closed that gap: renames no longer writes the manifest, and the
content check compares against the plan as reviewed. The content check
reads the plan's value from the path and the note together. It is per row. A row with no
collision has the same paths under both values and still applies. A run without the flag
plans the same paths as before, and it still accepts a free path typed into the manifest
by hand, including one shaped like `Shared.A.cs`. Such a row's note does not name that
path, so the check does not fire. The cost is a dependency on the note text, and it cuts
both ways. Strip the note and the check goes quiet for that row. Rewrite the note as well
as the path, for example with a replace-all of `A.Shared.cs` to `Shared.A.cs`, and a run
without the flag refuses that row. That one hand edit is the only default behavior that
changed. Under the `metadata` convention `Shared{T}.A.cs` still raises SA1649,
for the same reason `Shared{T}.cs` does.

A forgotten flag is the likely mistake, and it is not free. The mismatched rows are
skipped, so the run exits 0, and the note on each row names the cause. Those rows go to
the report and the plan keeps them as `split`, so the fix is to run the content phase
again with the flag. Before decision 23 the content phase wrote them back as `skipped`,
which used up the reviewed plan.

Type-first refuses a colliding source whose base name is `G`, `Designer`, or `Generated`,
or ends in `.g`, `.g.i`, `.designer`, or `.generated`. The qualified name would end in a
suffix that Roslyn's
`GeneratedCodeUtilities.IsGeneratedCodeFile` reads as generated code. Analyzers skip such
a file and nullable annotations switch off in it. File-first has the mirror case when the
type is named `G`, `Designer`, or `Generated`. It is left alone so default plans stay
identical.

**Reversal warning.** Remove the content check and the flag stops meaning anything in the
content phase. That phase takes paths from the manifest and never reruns the collision
pass, so it would write whichever form the manifest holds and report `split`. Drop the
note half of the check and a default run starts refusing every hand-edited `Shared.A.cs`,
not only one whose note was rewritten to match. The pass that writes the note and the
check that reads it both call `CollisionNames.FormatResolution`. Give either its own
format and the check stops matching. Widen the check to "every manifest path must be one
this run computes" and every hand-edited path is refused. Flip the default and every
existing plan with a collision goes stale at once. The flag name and its two values are
now command line contract. Rename or remove one and every script that passes it exits 2.

### Decision 22. The plan manifest carries a content hash of every input

**Context.** The content phase compared its recomputed plan against the manifest by shape
only: kept path, git move flag, output paths. An edit that kept every type name and path,
such as a changed method body, passed that check. The tool then applied a split to bytes
nobody had reviewed. The maintainer chose to close this with a hash that is always on.

**Decision.** Every manifest row for a file the plan read records `contentHash`, the SHA-256 of the input's raw
bytes, written as `sha256:` plus 64 lowercase hexadecimal digits. It is the last CSV column
and the last field of each JSON file entry. One rule governs it: **a run that starts a
plan records hashes, and every later phase only carries and compares them.** The details
below follow from that rule.

- **Raw bytes, not decoded text.** The hash is taken from the bytes `FileSplitter` already
  reads and hands to `EncodedSource`, so planning reads the file once. A change of
  encoding, byte order mark, or newline style therefore counts. The tool re-emits all of
  those, so the plan was reviewed against them too.
- **Which rows carry it.** Every row for a file the plan read, whatever its status. A row
  is empty only when the plan never read the file: excluded, missing, or unreadable. One
  rule with no status cases is easier to keep true than a rule per status.
- **Later phases never write their own hash.** Renames and content each write their rows
  back over the manifest. If either stored the hash of the bytes it found, a refusal
  would match on the next run and turn into permission. So a row written by renames or
  content takes its hash from the manifest row it replaces, on every status and every
  exit, including the exits that never read the file. A `split` row written by content
  still shows the planned hash, not the hash of the rewritten kept file. Decision 23
  stopped both phases writing the manifest. The rule stays for the rows they report, so
  a report never shows a hash the plan phase did not record.
- **A mismatch is a `skipped` row, not `failed`.** It matches the structural mismatch,
  which was already a skip, and decision 13: refusing is the correct outcome. The reason
  starts with `stale plan:` so a caller can find these rows without parsing hashes. In
  the content phase the check runs straight after the read, before parsing and every
  guard that follows it, and nothing is written.
- **Renames is held to the plan.** The renames phase recomputes the plan, and it used to
  apply whatever it computed. When a manifest exists it now refuses, with a `stale plan:`
  reason and no rename, a file whose bytes differ from the recorded hash, a file it would
  split that the manifest marks `skipped` or `failed`, and a file it would split that the
  manifest has no row for. The byte check applies to every file renames reads that has a recorded hash, not only
  the ones it would split, so a changed file is reported as changed. That covers a file
  the plan skipped that later gains a type. Before decision 23 it also covered a file
  content refused as stale and wrote back as `skipped`. A `git mv` does not change bytes, so a renamed file still matches in
  the content phase. Renames is held to the hash and the status, not the shape: with
  matching bytes it still applies the paths it recomputed.
- **A manifest with no `contentHash` column ends the run.** Content and renames both exit
  3 with a message that says to re-run the plan phase, before any source file is read or
  moved.
  See Rejected.
- **An empty or malformed value refuses that one file.** It is a `skipped` row with the
  same prefix. The comparison is ordinal against one canonical form, so uppercase digits,
  a short value, or another algorithm's prefix never match by accident and never throw.
- **The structural check stays.** The hash proves the source is unchanged. It says nothing
  about the manifest. The structural comparison now catches an edited manifest over
  unchanged source, which `ContentHashCarryTests` pins.

**Rejected.** Falling back to the structural check when the column is missing. That is the
silent path: a caller with an old manifest would get the old, weaker guarantee and no sign
of it. Skipping every row for a missing column was also rejected. A run that skips
everything exits 0 and looks like a run with nothing to do, and the cause is one fact about
the file, not one per row. An opt in flag was rejected by the maintainer: a safety check
that is off by default protects nobody who has not read about it. Hashing decoded text was
rejected because it would pass a newline or encoding change the plan never showed. Letting
renames check only the rows it was about to rename was tried first and rejected in review.
Each row status it did not check was a path by which a fresh hash reached the manifest.

**Consequences.** This is a manifest and JSON schema change. A manifest written by an
earlier version cannot drive the renames or content phase; re-run the plan phase. A reader
that addresses CSV columns by position keeps working, because the column is appended. A
reader that rejects unknown columns or JSON fields needs an update. Renames now reads the
manifest when one exists. It used to ignore and overwrite it. Renames no longer renames a
file added to the input after the plan; re-run the plan phase to include it. The suite now
starts `git` in several tests.

Three limits remain.

1. The phases trust the manifest on disk. Someone who edits a file and then edits its
   `contentHash` to match defeats the check, and so does deleting the manifest before
   renames. The hash detects drift. It does not authenticate the manifest.
   A copy of a planned file has the same hash, so identity comes from the path. Renames
   accepts a kept path only once the original is gone.
2. Renames with no manifest on disk plans and hashes afresh, as it always could. Nothing
   was planned, so there is nothing to hold the file to.
3. A second content run, or a renames run, over a file content already split reports it as
   `stale plan:`. The first run rewrote the kept file, so its bytes differ from the plan,
   and the tool cannot tell its own write from an outside edit. Before the hash, that
   second run reported `nothing to split`. The file is left alone either way. Decision 23
   changed the content half: a content retry now reports such a file as `already applied`.
   A renames run still reports `stale plan:`.

**Reversal warning.** Remove the content check and an edit made after review is applied
with no sign, as long as type names and paths survive it. Let renames or content store the
hash of the bytes it found, on any row, and a refused edit matches on the next run. Let
renames act on a file the manifest does not mark `split` and the same edit is planned
afresh one phase early. Make the missing column a fallback and every old manifest silently
loses the guarantee. Hash decoded text instead of bytes and a newline or encoding change
slips through.

### Decision 23. Only the plan phase writes the manifest, and runs report to their own file

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
- **The checks fail closed.** Every look at a path has three answers: something is there,
  nothing is there, or the look failed. A fourth version asked `File.Exists`, which
  answers false for the last two. A manifest directory about 4,060 characters long made
  the look for the probe fail, that read as a different directory, and the report
  replaced the plan. Only a look that proves absence counts as absence now.
- **A manifest path must end in a file name.** A fifth version compared the two file
  names as text and met `--manifest plan.csv/`. The operating system still found the
  file, the last part of the path read as empty, and the names looked different. The
  renames phase did not open the manifest then, so the report replaced the plan. A trailing
  separator on `--manifest` is now a rejected command line, and `ReportFile` refuses a
  manifest path that ends in one or names a directory.
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
  longer writes the manifest. Its rows go to the report. Decision 22 holds it to the
  hash and status of each manifest row when a manifest exists, and that stays. A rename has to follow the file
  as it is now, and the content phase already refuses a row whose rename state disagrees
  with the plan, so nothing downstream depended on the rewrite. Decision 19 needs the
  kept path of each `split` row, and the plan phase already records it. A rename that
  fails is now a `failed` row and exit code 1. It used to end the run with exit code 3
  after earlier renames had landed, which left no report to name them.
- **A retry skips what is already done.** A row whose planned outputs all exist, and whose
  file still declares a type but none of the planned ones, is `skipped` with the reason
  `already applied: every planned output exists and this file no longer declares the
  planned types`. Such a file no longer matches the hash decision 22 records, so the
  check sits inside the hash check and only picks the reason: `already applied` for this
  case, `stale plan:` for every other change. Either way the file is skipped and nothing
  is written. Decision 22 had listed the stale reason on a second content run as a limit.
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
question, and a file that fails it is refused as a stale plan. A file whose bytes still
match the plan never reaches the check, and meets every existence check as before.

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

**Known limits.** A manifest directory within about 40 characters of the path limit
makes the look for the probe fail. A report with the manifest's file name is then
refused even when it sits in another directory. The file system can give one file two names in one directory, such as
a Windows short name or a second Unicode spelling on macOS. A report path that uses the
other name is not seen as the manifest. A named pipe at the report path makes the format
check wait for a writer. A manifest reached through a symbolic link must be passed by its real
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
longer declares its type. A retry reports that file as `stale plan:`, which is a skip at
exit code 0. A renames run over a file content already split also reports `stale plan:`.

**Reversal warning.** Write a later phase's rows back to the manifest and a failed row
erases its own plan again. The retry then reports the file as not planned, at exit code 0.
Remove the already applied check and a retry still exits 0, but every applied row reads
`stale plan:`, which a caller cannot tell from a file someone edited.
Widen that check into an exemption from the existence checks and an edited manifest can
aim a write at an existing file, which rule 10 forbids. Replace a three way look with
`File.Exists` and a path the system cannot look up reads as absent, which is how the
report last reached the plan. Open the report path directly,
to probe it or to write it, and a report path that is a hard link to the manifest empties
the plan. Drop the check at the write and a report can replace a source file the run just
created.

### Decision 24. The file naming convention is a flag, and the content phase refuses the other convention's names

**Context.** StyleCop SA1649 has two file naming conventions. `stylecop`, the default,
accepts `Foo{T}.cs` or `Foo.cs` for `Foo<T>`. `metadata` accepts only ``Foo`1.cs``. The
tool emitted the first form only. In a `metadata` repository, every generic file it wrote
raised SA1649 and had to be renamed by hand.

**Decision.** `--file-naming stylecop|metadata`, default `stylecop`. Under `metadata` a
generic type or delegate is named ``Name`N.cs`` with N its arity, and a type that is not
generic keeps `Name.cs`. That matches `FileNameHelpers.GetConventionalFileName` in
StyleCop.Analyzers, read at commit `45026329`. A build with `1.2.0-beta.556` confirmed
it. The delegate case is newer than `1.1.118`, whose `FileNameHelpers` source has none.
Two planner rules follow from the name. The kept type is the one whose arity name matches
the file, so `Foo<T>` stays in ``Foo`1.cs``.
A generic type kept in a plain named file is renamed, so `Foo.cs` becomes ``Foo`1.cs``.

**Rejected.** Reading `stylecop.json` or `.editorconfig`. That is decision 14 again: the
tool would guess at one repository's layout, and a settings file it failed to find would
change file names without a word. The caller knows the convention and states it.

**Rejected for now.** Recording the convention in the manifest. Decision 21 rejected a
column for `--collision-naming` and said to revisit it if a second flag ever changed
planned paths. This is that flag, so the trigger has fired. Decision 21's cost argument is
also weaker now: decision 22 has since added a column to the manifest.

A column would buy three things. The renames phase reads the manifest since decision 22, so
it could compare a recorded value with its flag and refuse before any `git mv`. A mismatch
note could name its cause with certainty. And a path a reviewer typed by hand could be told
from a plan made under the other value, which removes the one false refusal recorded below.

This change still does not add it. A column that records one naming flag and not the other
would be half a design. The right change records both, checks both in renames and content,
and replaces the two name-inference checks that decision 21 and this one each added. That is a
manifest schema change across two features, and it deserves its own decision and review.
Until then this decision carries the cost. Two events reopen it: a third flag that changes
planned paths, or a report of a renames run that left out a planned rename because a flag
differed. The guides already tell a `metadata` repository to pass the flag to every phase,
so that advice is not the trigger.

Since then, decision 26 added a `collisionNaming` column, so `--file-naming` is the only
naming flag the manifest does not record.

**Consequences.** The content phase gained a check. It writes the paths the manifest
holds, so a plan made under the other convention was applied as it stood. It differs only
in a moved file's name, and the path set comparison runs after those paths are substituted
in. A moved type's manifest path is now refused when it is the name the other convention
gives that type, alone or in either cross-file qualified form. A changed kept path or
rename was already refused by the shape comparison.

Every other free path a reviewer types into the manifest is still applied. Decision 21
pins that for collision names with `ContentPhase_StillAcceptsAHandEditedFreePath`, and an
earlier draft of this check broke it by refusing any path the run had not computed. One
edit is refused that used to be applied: typing the other convention's exact name by hand.
The manifest records no convention, so nothing tells that edit from a plan made under the
other value.

The reason stays `content phase plan does not match manifest`, as for decision 21. The
skipped row's note says what differs: both flag values and both paths for a moved file,
the two kept paths for a kept file, and the path or rename for other shape drift. The row
goes to the run report and the plan keeps its `split` row, as decision 23 arranged. The
existence and uniqueness checks still run first, so `ManifestPathTamperingTests` reports
the same reasons.

The two flags compose. Under `metadata` with `--collision-naming type-first`, a colliding
`Shared<T>` from `A.cs` becomes ``Shared`1.A.cs``. SA1649 reads that as ``Shared`1`` and
accepts it. A build with StyleCop.Analyzers `1.2.0-beta.556` confirmed it.

Under `metadata`, `Box<T>` and `Box<U>` in one file share ``Box`1.cs`` and are refused as
`target path collision within split`. Under `stylecop` they split by parameter name.

The content check covers one of two ways to disagree. Plan and renames under one value,
content under the other: a file the two conventions plan differently is skipped and nothing
is written for it. A file they plan the same way still splits, which covers every file
with no generic type. The run exits 0 and the plan is unchanged, so recovery is to run the
content phase again with the plan's value. The mismatch note says so first, and offers a
new plan as the other way out.

The renames phase is held to one path: the kept-file rename. It runs a rename only when the
manifest row plans that same rename, to that same target. The content phase reads the kept
path the manifest holds, so any other rename would strand the file.
`KeptRenameCollisions.HoldToPlannedRename` enforces it. A dropped rename leaves the file
where the plan left it, and the row's note says why. That closes the worse half of a flag
mismatch in the renames phase: it can no longer move a file the plan left in place, or
move one to the other convention's name. The other half stays open. A renames run under
another value can leave out a planned rename and report the row as `split`. The content
phase then fails that row and says to run the renames phase, and a renames run with the
plan's value recovers. `AGENTS.md` lists this as a known issue.

`metadata` also renames more kept files, which exposed an older gap. Two inputs could plan
one rename target, because the per-file plan only sees targets that already exist. A
generic type split across `Foo.cs` and `Foo.Extra.cs` plans ``Foo`1.cs`` twice. Both rows
showed as `split`, the renames phase moved the first, and `git mv` failed on the second.
Before decision 23 that stopped the run at exit 3. Since then it is a `failed` row at exit
1. Either way the plan promised a rename that could not happen. `KeptRenameCollisions` now
runs in the plan and
renames phases. The first input that is still `split` keeps the rename. Each later one
keeps its original name with the note a taken target always got, `rename collision for
<path>; kept original file name`. This applies without the flag too, where the same inputs
used to fail.

Refusing both inputs was the other choice, and it was rejected. It would hand back a
common layout, a generic type in `Foo.cs` and `Foo.Extra.cs`, for manual work. Keeping the
original name is what the tool already did for a target that exists on disk, so the later
input gets a known outcome. Input order decides the winner because nothing else about the
two files ranks them.

The pass has two rules that are easy to break. It runs after every pass that can refuse a
row, in the renames phase after the manifest binding too, so a refused row never holds a
target. And it compares targets the way the disk does, because the content phase asks the
disk. A dropped rename is only settled once the winner's rename lands. Before that, the
content phase skips the row and its note says to run the renames phase. When the winner is
refused as stale, or is missing, the later input does not take the rename. Its plan row
says no rename, so it waits with the winner for a new plan. On `main` that later input was
renamed and split, because its own plan row carried the rename. Here the plan gives the
target to one input, and one refused file holds back both. A kept rename that
collides with another input's new file is still open, and `AGENTS.md` lists it.

**Reversal warning.** Remove the other convention check and a content run whose flag
differs from the manifest it was handed applies that manifest at exit 0, with names from
the other convention. Widen it to refuse every path the run did not compute and decision
21's hand-edited path test fails. Drop the kept type rule and every correctly named
``Foo`1.cs`` loses `Foo<T>` to a new file, which is the opposite of what the flag is for.
Change the default and every existing plan and test that involves a generic type changes
names. Remove `KeptRenameCollisions` and two inputs with one rename target plan a rename that
fails. Remove `HoldToPlannedRename` and the renames phase can move a file the content
phase will then not find. The flag name and its two values are command line contract: rename or
remove one and every caller that passes it exits 2.

### Decision 25. A run with no phase plans

**Context.** `--phase` defaulted to `content`, the one phase that rewrites source. No entry
in this log recorded why. Every doc had to warn the reader to pass `--dry-run`, and the
package `AGENTS.md` repeated it. A caller that skips the docs runs
`split-types --input .` first, and that first call rewrote the repository. Agents make that
call. The NuGet team's skill evaluation found that weaker models follow written guidance
less reliably than frontier models. That finding is about following guidance, so applying
it to a bare first call is an inference, not a measurement. A safe default protects every
tier either way, and costs a frontier model nothing.

**Decision.** With no `--phase` and no `--dry-run`, the tool runs `plan`. Only
`--phase content` rewrites source. `--dry-run` stays as an alias for `--phase plan`, and
combining it with `--phase` is still exit 2.

**Rejected.** Requiring `--phase` on every run. It is safer than any default, but the
first call then fails with exit 2 and teaches the caller nothing it could not learn from a
plan. A plan teaches the caller what would happen. Removing `--dry-run` was rejected too:
it breaks every script and doc that uses it, for no gain.

**Consequences.** Breaking. A script that relied on the old default now plans, exits 0,
and changes nothing. Three signs show it: the summary line on standard error starts with
`plan:`, standard output carries the manifest CSV, and a `Next:` line names the phase to
run (decision 27). Exit 0 with no source change is accepted here, where decision 26 rejects
it. Here the run did exactly what it was asked, and says what to do next. There, a run
asked to apply a plan skipped rows without saying why on the exit code. Docs now tell
readers to pass `--dry-run` or `--phase` explicitly, so a command means the same on
version 0.1.1 and on later versions. A published version is immutable, so restoring the
old default would itself be a breaking change in a later version. `--report` is refused in
the plan phase (decision 23),
so `--report` with no `--phase` is now exit 2.

**Reversal warning.** Restore `content` as the default and the first bare call from an
agent rewrites source before anyone has reviewed a plan.

### Decision 26. The plan records `--collision-naming` in the manifest

**Context.** Decision 21 made the value a flag that every phase had to repeat, and it
rejected a manifest column because the column "changes the manifest CSV for every caller,
to serve one flag." Two facts changed. Decision 22 then added `contentHash`, so the CSV
already changed for every caller, and no released version writes that column. And the
forgotten flag was the trap decision 21 predicted. A content run that forgot it skipped the
collided rows and exited 0. Before decision 23, it also overwrote their reviewed plan, and a
renames run that forgot it replaced the reviewed names in the manifest. Decision 23 ended
both overwrites, because only the plan writes the manifest now. The skip remains: exit 0
reads as "nothing to do", and a caller that does not read every row's reason stops there.
A renames run under the other value still computes paths the plan never held. The
maintainer chose to make the break.

**Decision.** Every manifest row carries `collisionNaming`, `file-first` or `type-first`,
appended after `contentHash`. The JSON report carries the same value once, as a top level
field. The rules, all in `CollisionNamingFlag.Resolve` and `ManifestCollisionNaming.Read`:

- The plan phase uses the flag, or `file-first` when it is absent, and records that.
  It never reads an existing manifest's value.
- Renames, when a manifest exists, and content use the recorded value when the flag is
  absent. A flag that matches is accepted.
- A flag that contradicts the recorded value ends the run with exit code 3, before any
  source file is read or moved. The error names both values and says what to do.
- A missing column, or a blank, unknown, or mixed value, ends the run with exit code 3.
  Each of those means the file was edited or written by an older build. Both are cases
  decision 22 settled: no silent fallback.
- `ManifestWriter.Write`, `ToCsv`, and `ReportFile.Write` take the value as a required
  parameter. An optional one could be forgotten by the next caller, and the column would
  then carry a default nobody chose. The run report carries the column too, because it is
  the same CSV format, so a report shows which naming the run applied.
- `FileSplitter.Run(out CollisionNaming)` hands the resolved value to `Program`. The
  plain `Run()` remains for tests that do not need it.
- `Options.CollisionNaming` is nullable. Null means "absent", which is not `file-first`
  once a manifest exists.

**Rejected.** Inferring the value from the paths and notes the plan wrote. A manifest with
no collision carries no evidence either way, and the inference would depend on note text,
which decision 21 already calls a cost. A sidecar file beside the manifest was rejected:
two files that must travel together will be separated. Keeping the per-row skip for a
contradicting flag was rejected for the exit 0 reason above.

**Consequences.** Breaking, for the manifest and the JSON. A manifest written before this
change cannot drive renames or content: it lacks the column, and the run says to re-run
the plan. The `contentHash` check runs first, so a 0.1.1 manifest reports that error,
which gives the same advice. A reader that rejects unknown columns or JSON fields needs an
update. The per-row check from decision 21 stays. It is now reachable only when someone
edits the column and leaves the paths and notes alone, and
`CollisionNamingContentTests` pins it through exactly that edit. `CollisionNamingRecordedTests`
runs plan, renames, and content through the real executable with the flag only on the plan,
and checks that a contradicting flag leaves the manifest, the sources, and the report path
untouched. `ReportFile.Prepare` runs before the check and probes the report directory, but
it never creates the report file. `MetadataCollisionGuardTests` pins both guards on a backtick
name from decision 24.

Decision 21 said to revisit a manifest column "if a second flag ever changes planned
paths". `--file-naming` (decision 24) is that second flag, and it landed on main while this
change was in review. It is not recorded here. `nextArgs` repeats `--file-naming metadata`
for the next phase (decision 27), so a caller who follows it cannot drop the flag.

Decision 24 called a column for one naming flag and not the other "half a design". This
change adds that half anyway, for two reasons. The collision flag's failure was observed:
a forgotten flag skipped rows at exit 0, and before decision 23 it also rewrote reviewed
names. And the column, the strict read, and `CollisionNamingFlag.Resolve` are the pattern a
`fileNaming` column would reuse. Recording `--file-naming` the same way is the named
follow-up, and whether to do it is an open decision for the maintainer.

**Reversal warning.** Drop the column and every phase must be told the value again, with
the exit 0 trap back in place. Let `Write` default the value and a future caller records
`file-first` for a type-first plan. Resolve a contradiction by preferring the flag and a
renames run computes paths the plan never held. Accept a blank value as "not recorded" and an edited
manifest silently falls back to the flag.

### Decision 27. The report says what to run next

**Context.** The workflow is three runs with the same options, and the commonest mistake is
a dropped option between them: a missing `--manifest`, a missing `--exclude`, or before
decision 26 a missing `--collision-naming`. A frontier model reads the docs and carries the
options forward. An economical model is expected to follow one literal command more
reliably than a paragraph of advice. The NuGet team measured lower skill compliance in
weaker models, which points the same way, but no evaluation of this tool has measured it.

**Decision.** The JSON report carries `nextArgs`, the argument list for the next phase,
and standard error carries a `Next:` line with the same suggestion. `NextStep.For` decides.
After `plan` it points at `renames` only when a row needs a `git mv`, and at `content`
otherwise. It is `null` after `content`, after a failed row, when nothing splits, for
standard input, after a renames run with no plan on disk, and when the report file could
not be written. It carries `--report` only
when the caller chose a path, because the parser fills in the default otherwise. It carries
`--file-naming metadata`, because the manifest does not record that flag (decision 24), and
a content run under the other convention skips the affected rows.
`--help` now prints the three step workflow and says that exit 0 can mean nothing happened.

The `Next:` line prints an argument bare when it holds only letters, digits, and
`_ . / : + -`, and in single quotes when it also holds a space or a backslash. Bash, zsh,
and PowerShell all read single quotes literally. Any other character, such as `$`, a
backtick, `&`, a quote, `=`, `@`, `,`, `%`, or a line break, turns the line into a sentence that
points at `nextArgs`. Review found that the first version used double quotes, where `$`
still expands, and later that zsh expands a leading `=` and PowerShell splats a leading
`@`.

`nextArgs` being `null` does not mean the split is done. A repeated renames run can end
with `null`, for example when every split row was a rename and now says `input file does
not exist`. The packaged skill therefore defines
done positively: the last report's `phase` is `content`, `summary.failed` is 0, and every
skipped row's reason is `not present as split in plan manifest` or starts with `already
applied`. Those are the only content skips that leave no planned work behind. Three review
rounds each found one more skip reason that an exclusion list missed: `stale plan:`, then
`content phase plan does not match manifest`, then `planned as split but not supplied to
the content phase input`. An allow list of the two harmless reasons ends that pattern.
`FileSplitter.NotPlannedReason` and `AlreadyAppliedReason` are `internal` so that
`PackagedAgentDocsTests` can fail when the skill stops quoting them. The skill's fallback
repeats every option the plan had, `--file-naming` included.

**Rejected.** A full command line string. The command name differs by install method, and
quoting differs by shell, so a string would be wrong for some caller. An argument array
starting at the verb is right for all of them. Pointing at `renames` after every plan was
rejected because a renames run with nothing to move is a wasted turn. Replaying `--input -`
was rejected because the second process reads an empty stream and reports success.
Suggesting a phase after a failed row was rejected, because a failure needs a decision.

**Consequences.** Additive for the JSON: a reader that rejects unknown fields needs an
update. Standard error gains one line on success. Standard output is unchanged, so a pipe
into a parser still works. A frontier model can ignore the field at no cost.

**Reversal warning.** Add `--collision-naming` to `nextArgs` and it repeats a value the
manifest already records; after a fresh plan under another value, the copied flag would
end the next run at exit 3. Return a suggestion for standard input and the next run plans
nothing and exits 0. Suggest content after a renames run with no plan on disk and the
next run exits 3. Add `=`, `@`, `,`, or `%` back to the bare set and a pasted `Next:`
line breaks in zsh, PowerShell, or cmd.exe. Move the field after `files` and a truncated read loses it.

### Decision 28. The package ships an Agent Skill as well as `AGENTS.md`

**Context.** The .NET team now delivers Agent Skills inside NuGet packages, at
`skills/<name>/SKILL.md`. Restore puts the file on disk, and `dotnet-inspect` lists it.
This package already shipped `AGENTS.md` at its root for NuGet MCP. Whether the skill
convention works for a tool package was tested rather than assumed. The results are in
[agent-skills-in-nuget.md](agent-skills-in-nuget.md).

**Decision.** Ship `package/skills/split-types/SKILL.md` at `skills/split-types/SKILL.md`.
Keep the full workflow in `package/AGENTS.md` too, and have it name the skill.
`PackagedAgentDocsTests` checks both files: every documented command parses under the
current `Options`, and the skill meets the format rules. CI and the release workflow run
`.github/scripts/verify-package-contents.sh`, which matches exact entry names.

**Rejected.** Shrinking `AGENTS.md` to a pointer at the skill. NuGet MCP returns
`AGENTS.md` and does not read skills yet, so its users would get a pointer and nothing to
follow. Several skills, one per phase, were rejected: `nextArgs` already walks a caller
through the phases, and every extra skill is more text every model has to read. A
`scripts/` folder was rejected because the tool is the script.

**Consequences.** Two files describe one workflow, and they can disagree in prose. The test
only proves their commands still parse. Project-graph tools never see the skill: a tool
package cannot be a `PackageReference`, and restore says so with `NU1212`. Agents find it
through `dotnet-inspect package`, through the install path, or through a copy committed
to a repository. No evaluation has shown the skill helps any model. Run the evaluation in
[agent-skills-in-nuget.md](agent-skills-in-nuget.md) before the first release that ships
the skill is a month old, or sooner if a user reports higher cost; dropping the skill
after that is a next-version change. The NuGet team reports
small gains for frontier models, and warns that a skill can raise cost.

A release run checks out the tag it ships. A tag made before this change has no
`verify-package-contents.sh`, so re-running the release workflow for it stops at that step
before anything is published. `v0.1.1`, the only such tag, is already on NuGet.

**Reversal warning.** Move the `PackagePath` and the entry name changes. Two plausible
values were wrong, and only the exact match in `verify-package-contents.sh` caught them.
Drop the workflow from `AGENTS.md` and NuGet MCP users lose it. Delete
`PackagedAgentDocsTests` and the next renamed flag leaves both documents giving commands
that exit 2.

### Decision 29. Nested namespaces are walked to any depth

**Context.** `TopLevelType.Find` looked one namespace down and no deeper. A type in a
nested block namespace was never a split candidate. Each output kept the nested block as
shared text, so the type was declared once per output. The build then failed with a
duplicate type. The verifier did not catch it, because it compares outputs against the
same list of found types, and text outside every found type counts as shared.

**Decision.** `Find` walks nested namespaces to any depth, in document order. A nested
type is placed like any other type, and every guard now sees it.

**Consequences.** Some inputs that were skipped as `nothing to split` now split. One is a
file with one outer type and one nested type. Another is a file whose only namespace
holds a nested namespace with two types. The emptied nested block stays in each other
output, which matches how a sibling namespace block is already left. When two types at
two depths get one file name, the plan skips that input as a collision. Which pairs do
depends on `--file-naming`. Under `stylecop` the name and type parameter names must
match, so `Box<T>` and `Box<U>` split. Under `metadata` the name and arity must match,
so the same pair collides on ``Box`1.cs``. `NestedNamespaceTests` pins all of this.

**Reversal warning.** Stop the walk at one level and nested types are copied into every
output again, at exit code 0, with no guard to stop it.

### Decision 30. A file that holds a `file` type is split around it

**Context.** Decision 13 lists `file` scoped types among the inputs refused whole. So a
file with one `file` helper beside several ordinary types was left whole for a human. No
count of such files was taken. Three facts were measured before this changed. The build was a scratch project
on `net10.0` with StyleCop.Analyzers `1.2.0-beta.556`, and only SA1402 and SA1649 enabled.

1. **SA1402 flags a file that holds a `file` class beside another class.** `internal class
   Alpha`, `file class AlphaHelper`, and `internal class Sibling` in `Alpha.cs` raised
   SA1402 at lines 8 and 12. After `Sibling` moved out, the warning at line 8 was still
   there. No split makes such a file SA1402 clean. In these builds SA1402 left alone the
   class the file is named for, or the first class when none matched, and flagged each
   other class. Two `file` classes beside `Alpha` raised two warnings, and a `file record`
   raised one. By default SA1402 checks classes and records only. A `file` struct,
   interface, enum, or delegate beside one class raised nothing, and so did a `file class`
   beside one struct.
2. **Renaming the file changes the `file` type's metadata name.** `typeof(AlphaHelper).FullName`
   was `Probe.<Alpha>F6987AE8...__AlphaHelper`. After `Alpha.cs` became `AlphaRenamed.cs`
   it was `Probe.<AlphaRenamed>FD638230...__AlphaHelper`. An edit to the file's text left
   the name alone.
3. **SA1649 fires when the `file` type is declared first.** With `file class AlphaHelper`
   above `internal class Alpha` in `Alpha.cs`, SA1649 reported line 3. That is a condition
   of the source, and the tool does not fix it.

**Decision.** This amends decision 13. A `file` type no longer refuses the whole input.
Decision 13's rule stands: the tool splits only what it can prove safe, and never guesses.
The split is on by default and has no flag.

- A `file` type never leaves its file, and that file is never renamed. `gitMove` is false
  for these inputs in every phase.
- The kept type stays with the `file` types, and every other ordinary type moves. If one
  type that would move fails the proof, the whole input is skipped.
- The keep rule picks the kept type among the ordinary types. When no ordinary type is
  named for the file, that rule only takes the first one. Then the one ordinary type that
  names a `file` type is kept instead. This is safe because such a type can never move:
  keeping it turns a refusal into a split whose moved types all pass the same proof. With
  two such types one would have to move, so the input is skipped.
- A moved type is refused when any identifier in it equals a `file` type's name. The scan
  compares the identifier's value, so `@Name` and a Unicode escape match. It reads
  documentation comments, where a `cref` lives. It matches an attribute written without
  its `Attribute` suffix. A match counts even when it looks unrelated, because a `file`
  type hides a same-named type from a namespace or a using directive.
- Some inputs are refused before any type is chosen to move:
  - A `file` type declares an extension method or an extension block. These bind with no
    name at the call site: `foreach`, `await`, deconstruction, collection initializers,
    and query syntax all do it. No name scan can see that.
  - A using directive names a `file` type, as an alias or with `using static`. Every
    output repeats every using directive, and rule 1 forbids pruning them.
  - The input holds text a preprocessor directive excludes. The tool parses with no
    symbols defined, so a scan cannot read that text.
  - The input declares at most one ordinary type. Nothing can move.
- The split row's `note` says which `file` types stayed and whether SA1402 still flags
  the file. The brief asked for a note that says SA1402 still flags it. Finding 1 narrowed
  that: the warning needs two classes or records in the kept file. So the note says `SA1402
  still flags the file` when the kept file holds two, and `SA1402 flags the file only
  where it checks more than classes` when it does not. A note that promised a warning the
  build never raised would send a caller to suppress nothing. The note carries no path,
  because the content phase searches notes for collision paths.
- `OutputVerifier` asks again about the rendered text. One output must be written to the
  path that was read, and no other output may declare or name a `file` type.
- A `file` type may sit at any namespace depth. Decision 29 made every such type a type
  the tool places and scans, so no rule here depends on depth.

The maintainer's brief named the first two refusals in that list. The other two were
added while building this, and the maintainer had not ruled on them when this was
written: excluded text, and nothing to split. So was the two-form note above. The kept type exception was allowed by the brief, not required.

**Why a name scan is enough.** Leaving the file changes two lookups for a moved type:
lookup of a simple name, and lookup of extension members. Three compiler rules close the
other paths, and each was observed in the same scratch build. CS9051 forbids a `file` type
in the signature of a member of an ordinary type. CS9053 forbids one as the base of an
ordinary type. CS9071 forbids two types of one name in one file. So a moved type can only
reach a `file` type by writing its name in a body, an attribute, an interface list, or a
comment. A binding made through a symbol is not a lookup by name. A `file` class named in a
`CollectionBuilder` attribute was still bound from another file. That is one observation,
not a survey of every attribute the compiler reads. `FileLocalPremiseTests` pins the three
rules and that observation, so a compiler that relaxes one fails a test.

**Rejected.** Moving the `file` type with the types that use it: the new file has a new
name, so the metadata name changes. An opt in flag: a first run would still skip every
such file, and the proof is the same either way. A semantic model: decision 1. Reading the
excluded text of an `#if`: it means parsing once for each symbol set, and the tool does
not know the sets.

**Consequences.** This is a breaking change to default results. A file that was `skipped`
with `contains file-local type; manual split required` is now `split`, or `skipped` with
one of five new reasons. That old reason is gone. One SA1402 warning stays for each `file`
class or record in such a file, and a person must suppress it. The scan is conservative,
so some safe inputs are skipped: a moved type that merely has a member named like the
`file` type, and any input with an `#if` branch the parse excludes. How many inputs each
refusal still skips was not measured.

One hole is known and is not closed here. A `file` modifier inside an excluded `#if`
branch is never seen, so that type is planned as an ordinary one. `AGENTS.md` lists it
under known issues.

**Reversal warning.** Each item below was tested by removing it and compiling what the
split would then write.

- Let the keep rule rename the file, and the `file` type's metadata name changes with no
  error. Reflection, logs, and serialized type names all read that name.
- Drop the name scan, and a moved type that uses the `file` type stops compiling. When
  another namespace holds a type of the same name, it compiles clean and binds to that
  type instead. `FileLocalNameScanTests` shows the binding change.
- Drop the extension refusal, and `foreach (var item in 3)` over a `file` class's
  `GetEnumerator` extension stops compiling in the new file. No identifier names the class.
- Drop the using refusal, and the new file carries an alias to a type it cannot see.
- Drop the excluded text refusal, and the split compiles in one configuration and breaks
  in another.
- Compare raw token text in place of the identifier's value, and `@AlphaHelper` passes
  the scan.
- Let the kept type exception apply when the file is named for a type, and that type
  moves out to a file of its own name, which is the file being read.
- Drop the check in `OutputVerifier`, and a planning mistake reaches disk. The planner is
  then the only guard.
- Reword the start of the note, and the packaged agent documents stop matching it.
  `PackagedAgentDocsTests` fails on that.

Re-check this proof when the C# language version or the Roslyn package changes. The tool
parses with the preview language version, so new syntax parses with no error. A new way to
bind to a type with no name at the use site needs a new refusal here. Extension blocks in
C# 14 were one.

## Credit

The approach owes a debt to Lizzy Gallagher's write up on Roslyn refactoring,
<https://lizzy-gallagher.github.io/_site/roslyn-refactoring.html>. The main departure is
decision 1: that article uses a workspace and a semantic model, and this tool deliberately
does not.
