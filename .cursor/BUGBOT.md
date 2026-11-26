# Review rules for this repository

Read `AGENTS.md` first. Its numbered rules and its traps are the review standard here.
Flag any change that undoes one of them.

The behaviors below are intended. Do not report them as bugs. Each line names the doc
that explains it. Paths are plain text on purpose, so Bugbot does not load whole docs.

## The split-types tool

- Cross file collisions compare output paths only. Two `Shared` types in different
  namespaces collide because they want one path. See `docs/sa1402-sa1649-naming.md`,
  section "Same type name in two namespaces".
- Two parts of one partial type in one file skip that whole file with
  `target path collision within split`. The tool never merges parts. See
  `docs/sa1402-sa1649-naming.md`, section "Two types in one file that map to one file name".
- An enum or a delegate is a top level type and moves like a class. The kept type is the
  one whose name matches the file, or the first type when none matches. See `docs/behavior-contracts.md`, section "What counts
  as a top level type", and `docs/fix-sa1402-whole-repo.md`, section "Split every file in three steps".
- SA1649 under the `metadata` convention rejects both `Foo{T}.cs` and `Foo.cs` for
  `Foo<T>`. It expects ``Foo`1.cs``. See `docs/sa1402-sa1649-naming.md`, section
  "If you use the metadata convention".
- The SA1402 suppression goes on the extra type, not on the type that matches the file
  name. See `docs/fix-sa1402-whole-repo.md`, section "How do I suppress SA1402 instead?".
- The before and after scan recipe chains every command with `&&`. A failed scan stops
  the chain, so a parse error never reaches the comparison. See `docs/verification.md`, section "Before/after repository scan".
- `dnx AgenticRoslynTool --` needs the `--`. Without it, `dnx` reads `--help` and other
  options as its own. See `README.md`, near the top.

## Repository tooling

- `.qualityrc.json` restates scanner defaults on purpose. Only the test file duplication
  floor differs. See the `.qualityrc.json` trap in `AGENTS.md`.
- `.github/dependabot.yml` covers NuGet only, with `open-pull-requests-limit: 0`. That
  keeps Dependabot to security updates. Renovate owns version updates. See the dependency
  bot traps in `AGENTS.md`.
