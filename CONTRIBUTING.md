# Contributing

## Before you start

Read [AGENTS.md](AGENTS.md). It lists the invariants that must not break and the traps
that have already cost someone time. It is written for AI agents, and it is the fastest
orientation for a human too.

## Build and test

```powershell
dotnet build -c Release      # must be 0 warnings, 0 errors
dotnet test  -c Release      # must be 32 passed, 0 failed
```

`TreatWarningsAsErrors` is on. A warning fails the build. There is no separate lint step.

The .NET SDK is pinned in `global.json` to 10.0.302 with `rollForward: latestMinor`, so
any 10.0 SDK at or above that version works, including a later minor band such as 10.1.
An 11.0 SDK does not.

## Commits

Use [Conventional Commits](https://www.conventionalcommits.org/).

```
<type>(<optional scope>): <imperative summary>
```

Types in use here: `feat`, `fix`, `docs`, `test`, `refactor`, `perf`, `ci`, `chore`.
Append `!` after the type for a breaking change.

Keep commits atomic. One logical change per commit, and every commit builds and tests
green on its own. Do not mix a refactor with a behavior change, and do not mix
documentation with code unless the documentation describes that exact code change.

Write the summary in the imperative: "add directive safety check", not "added" or
"adding".

## Style

- **No em dashes and no en dashes.** Not in code, comments, documentation, commit
  messages, or pull request bodies. Use a comma, a period, or rewrite the sentence.
- One top level type per file. The tool enforces this rule elsewhere, so it follows it
  here.
- Types stay `internal`. Tests reach them through `InternalsVisibleTo`. Never widen
  visibility to make a test compile.
- Package versions live in `Directory.Packages.props`. A `PackageReference` must never
  carry a `Version` attribute.
- No generated file banners, no timestamps, and no attribution to an AI tool.

## Tests

Every behavior change needs a test. Tests use xUnit and plain `Assert`. No assertion
libraries and no mocking frameworks.

Tests must be hermetic. Write only under a temporary directory and clean up on dispose.
`TempWorkspace` exists for this. Nothing may write into the repository working tree.

If a test exposes a bug in the tool, report the bug and leave the test failing. Do not
change the tool to make a test pass unless fixing that bug is the point of your change.

## Changing the splitter

`src/AgenticRoslynTool/FileSplitter.cs` holds the verification guards that make every
promise in this tool true. Before changing it, read
[docs/behavior-contracts.md](docs/behavior-contracts.md) for what each guard protects and
[docs/decision-log.md](docs/decision-log.md) for what breaks if you reverse a decision.

After any change there, run the end to end smoke test in AGENTS.md, not just the unit
tests. The tool splits its own source cleanly, so a run over `src/AgenticRoslynTool/`
should report every file skipped and exit 0.

## Pull requests

CI runs on every pull request against `main`: restore, build in Release, then test. It
must be green before merge. The workflow is `.github/workflows/ci.yml`.

Actions are pinned to a full commit SHA with the version in a trailing comment. A tag can
be moved, a SHA cannot. Keep it that way when you update one.

In the pull request body, say what changed, why, and how you verified it. If you found a
problem and chose not to fix it, say that too, and record it in the "Known issues" section
of AGENTS.md so the next contributor does not rediscover it.

## Releasing

The tool ships to NuGet as a .NET tool package. Publishing is tag driven: push a tag
matching `v*` and `.github/workflows/release.yml` builds, tests, packs, and pushes with
the version taken from the tag.

```powershell
git tag v0.2.0
git push origin v0.2.0
```

The tag must read `vMAJOR.MINOR.PATCH` with an optional prerelease suffix, for example
`v0.2.0` or `v0.2.0-rc.1`. The workflow rejects anything else before it builds, so a
malformed tag cannot reach NuGet. Republishing a version that already exists fails rather
than being skipped, because NuGet does not allow true deletion and a silent skip would
report a release that never happened.

The tag is the only place the shipped version comes from. `<Version>` in the csproj is a
fallback for local packing, so it does not need to match the tag. The workflow needs a
`NUGET_API_KEY` secret on the `nuget` environment. Required reviewers on that environment
gate the whole job, so an approver signs off on the tag before the package is built rather
than on a finished artifact.

Verify a package locally before tagging:

```powershell
dotnet pack src/AgenticRoslynTool -c Release -o artifacts
dnx --source artifacts --yes AgenticRoslynTool
```

If you rename `ToolCommandName`, the command every consumer types changes. That is a
breaking change for anyone scripting the tool, so treat it as one.
