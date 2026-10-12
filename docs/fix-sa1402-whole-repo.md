# Fix SA1402 across a whole repository: why dotnet format does nothing and how to split every file

`dotnet format` cannot fix SA1402 across a repository. StyleCop's SA1402 code fix has no
Fix All provider, because the fix creates new files and the batch fixer cannot do that. To
clear SA1402 everywhere, run `agentic-roslyn-tool split-types` three times: once with
`--dry-run` to plan, then with `--phase renames`, then with `--phase content`. Each
top-level type ends up in its own file.

## What SA1402 checks

[SA1402](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/SA1402.md)
fires when "a C# code file contains more than one unique type." Which kinds count is set by
`maintainabilityRules.topLevelTypes` in `stylecop.json`. The
[default is `["class"]`](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/Configuration.md#maintainability-rules),
so by default a delegate, enum, struct, or interface may share a file with a class. The
setting accepts `class`, `interface`, `struct`, `enum`, and `delegate`.

Two cases never trigger the rule. Parts of the same partial type may share a file. Types declared with
the `file` modifier are exempt.

## Why dotnet format and Fix All do nothing

The answer is in
[`SA1402CodeFixProvider.cs`](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/StyleCop.Analyzers/StyleCop.Analyzers.CodeFixes/MaintainabilityRules/SA1402CodeFixProvider.cs):

```csharp
public override FixAllProvider GetFixAllProvider()
{
    // The batch fixer can't handle code fixes that create new files
    return null;
}
```

The single fix exists. In the IDE you can move one type to its own file. There is no Fix
All, so each extra type takes its own click. That is fine for five files. It is not fine for
five hundred.

[`dotnet format`](https://learn.microsoft.com/dotnet/core/tools/dotnet-format) applies
analyzer fixes in bulk through Fix All, so it has nothing to apply. A sample project with
StyleCop.Analyzers 1.2.0-beta.556, the .NET 10.0.401 SDK, and one file holding two classes
gives this:

```console
$ dotnet format analyzers src/Shapes/Shapes.csproj --diagnostics SA1402
Unable to fix SA1402. Code fix SA1402CodeFixProvider doesn't support Fix All in Solution.
$ echo $?
0
```

The command exits 0 and leaves every file unchanged. In a CI log that reads as success.

Severity changes what you see. `dotnet format` only considers diagnostics at or above
`--severity`, which defaults to `warn`. SA1402 is a warning out of the box. If your
`.editorconfig` lowers it, for example to `dotnet_diagnostic.SA1402.severity = suggestion`,
the same command prints nothing at all and still exits 0. Pass `--severity info` and the
"Unable to fix SA1402" line comes back. Neither setting makes the fix run.

## Split every file in three steps

[AgenticRoslynTool](https://www.nuget.org/packages/AgenticRoslynTool) is a free .NET tool
that does the repository-wide split. It needs the .NET 10 SDK.

```bash
dotnet tool install --global AgenticRoslynTool
```

> [!IMPORTANT]
> The default phase is `content`, which rewrites source files. Always start with
> `--dry-run`. Never run `split-types` without `--dry-run` or `--phase`.

Run these from the repository root. The list file and the manifest live one directory up,
outside the repository, so a later `git add -A` does not commit them. The
`core.quotePath=false` setting makes Git print a path such as `Café.cs` as is, instead of
quoting it with octal escapes. The tool reads a quoted line as a path that does not exist and
skips it with `input file does not exist`.

```bash
git -c core.quotePath=false ls-files '*.cs' > ../split-types.txt

# 1. Plan. Writes ../split-types.csv and changes no source file.
agentic-roslyn-tool split-types --input ../split-types.txt --dry-run --manifest ../split-types.csv

# 2. Rename files whose name does not match their first type, then commit.
agentic-roslyn-tool split-types --input ../split-types.txt --phase renames --manifest ../split-types.csv
git commit -m "Rename C# files to match primary types"

# 3. Move every other top-level type into its own file.
agentic-roslyn-tool split-types --input ../split-types.txt --phase content --manifest ../split-types.csv
dotnet build
```

The same steps in PowerShell. Keep the forward slashes: backslash paths failed when this
block ran in PowerShell 7 on Linux.

```powershell
git -c core.quotePath=false ls-files '*.cs' | Set-Content -Encoding utf8 ../split-types.txt

agentic-roslyn-tool split-types --input ../split-types.txt --dry-run --manifest ../split-types.csv

agentic-roslyn-tool split-types --input ../split-types.txt --phase renames --manifest ../split-types.csv
git commit -m "Rename C# files to match primary types"

agentic-roslyn-tool split-types --input ../split-types.txt --phase content --manifest ../split-types.csv
dotnet build
```

Notes on each step:

1. **Plan.** Open `split-types.csv` and read it. Each row says whether a file will be
   `split` or `skipped`, and why. Nothing has moved yet, so you can run the plan as often as
   you like.
2. **Renames.** The tool runs `git mv`, which stages the rename, so `git commit` needs no
   `git add`. Keeping the rename in its own commit lets Git record a rename instead of a
   delete and an add, so file history survives. If the plan has no `gitMove` row, nothing
   is staged and `git commit` exits 1. Skip the commit in that case.
3. **Content.** This phase reads the plan manifest and refuses any file whose recomputed
   plan no longer matches it. It stops with exit code 3 if the manifest does not exist.

Feed every phase the same list file, not a directory. After the renames phase,
`Shapes.cs` is gone and `Circle.cs` exists. A directory scan in the content phase finds
`Circle.cs`, which the plan never named, and skips it with
`not present as split in plan manifest`. The list file still names `Shapes.cs`, and the
manifest tells the content phase where that file went.

Each phase rewrites the manifest at `--manifest` with what it did. Copy the plan aside
first if you want to keep it for comparison. Do not point the content phase at a new,
empty path: it needs the plan.

### What the split looks like

Before, `src/Shapes/Shapes.cs`:

```csharp
namespace Shapes;

public class Circle
{
    public double Radius { get; set; }
}

public class Square
{
    public double Side { get; set; }
}
```

After the renames phase it is `src/Shapes/Circle.cs`. After the content phase there are
two files. `src/Shapes/Circle.cs`:

```csharp
namespace Shapes;

public class Circle
{
    public double Radius { get; set; }
}
```

`src/Shapes/Square.cs`:

```csharp
namespace Shapes;

public class Square
{
    public double Side { get; set; }
}
```

Every emitted file gets the source file's using directives and namespace declaration.

### Reading the plan as JSON

Add `--json` to get one JSON document on standard output instead of CSV. This excerpt
comes from the plan for the sample above, with paths shortened:

```json
{
  "phase": "plan",
  "summary": { "total": 3, "split": 2, "skipped": 1, "failed": 0, "newFiles": 2 },
  "files": [
    {
      "originalPath": "/repo/src/Shapes/Shapes.cs",
      "keptPath": "/repo/src/Shapes/Circle.cs",
      "gitMove": true,
      "status": "split",
      "reason": null,
      "note": null,
      "newFiles": [{ "newFilePath": "/repo/src/Shapes/Square.cs", "type": "Square" }]
    }
  ]
}
```

The full document also carries `manifest`, the path of the manifest it wrote. A one-line
summary goes to standard error:

```text
plan: 3 input(s), 2 split, 1 skipped, 0 failed, 2 new file(s).
```

Exit codes: 0 means nothing failed, and a run that skips every file also exits 0. 1 means
at least one file failed. 2 means the command line was wrong. 3 means the run could not
start or finish.

## Check the result

Run these from the folder that holds your solution file, after the content phase:

```bash
dotnet build
git add -A
git diff --cached --check
dotnet format analyzers --diagnostics SA1402 --verify-no-changes
```

`git diff --check` alone skips the new files, because they are untracked until you add
them. On the sample, `dotnet format --verify-no-changes` exited 2 and listed the SA1402
warning before the split. After the split it exited 0, and the build reported no SA1402
warning.

The tool runs its own checks before it writes each file. It refuses to overwrite an
existing file, rejects splits that cross `#if` or `#region` boundaries unsafely, checks
that each type and its own comments land in exactly one output, and checks that no source
line was dropped. A file that fails a check is left untouched and marked `failed`.
[docs/verification.md](verification.md) explains what each check proves and gives a
before and after type scan you can run yourself.
[docs/behavior-contracts.md](behavior-contracts.md) lists every guard.

There is no run level transaction. Files are processed one at a time, so a failure late in
a run leaves earlier files already rewritten. That is why the plan comes first and each
phase gets its own commit.

## Files the tool will skip

A skipped file is left alone and its row carries a `reason`. These are the reasons you
are most likely to see:

| Situation | Status | `reason` |
| --- | --- | --- |
| File has top-level statements | skipped | `contains top-level statements; manual split required` |
| File declares a `file` type | skipped | `contains file-local type; manual split required` |
| An `#if` or `#region` spans two types | skipped | `<kind> group at line <n> crosses top-level type boundaries` |
| Two types share a name in one file | skipped | `target path collision within split: <path>` |
| File already has one type | skipped | `nothing to split: input has 1 top-level type declaration(s)` |
| Path matches `--exclude` | skipped | `excluded by pattern: <pattern>` |
| File does not parse | failed | `input has syntax errors: <diagnostics>` |

The same-name row covers partial types. SA1402 allows two parts of one partial class in a
file, but the tool sees two types that want the same file name and skips it. That is
harmless, since SA1402 does not flag that file, but the tool does not merge or move
partial parts. Two types with the same name that differ only by generic arity are split
into `Name.cs` and `Name{T}.cs`.

## Stricter than SA1402 by default

The tool moves every top-level class, struct, record, interface, enum, and delegate into
its own file. It does not read `topLevelTypes`. With the StyleCop default, this file is
not an SA1402 violation, but the tool still moves `WidgetKind` out:

```csharp
public class Widget
{
    public WidgetKind Kind { get; set; }
}

public enum WidgetKind
{
    Small,
    Large,
}
```

To keep such a file as it is, pass `--exclude` with part of its path, or leave it out of
the list file. Do this for the plan and for every later phase. Do not edit the manifest by
hand to drop a row: the content phase recomputes the plan and refuses a file whose plan
does not match.

## File naming and SA1649

The tool names each new file after its type. A generic type gets `{T}` in its file name
only when it shares a name with another type in the same file, as in `Name.cs` and
`Name{T}.cs`. When two source files in the same folder would each create the same new
file, the tool names both `SourceFile.TypeName.cs`. That form conflicts with SA1649, so
disable SA1649 in repositories that need it. The tool fixes SA1402. It does not fix
SA1649.

## FAQ

### Can dotnet format fix SA1402?

No. The SA1402 code fix has no Fix All provider, so `dotnet format` has nothing to apply
in bulk. It reports "Unable to fix SA1402" and exits 0.

### Why does Fix All not appear for SA1402 in Visual Studio?

For the same reason. The fix creates new files, and `GetFixAllProvider()` returns `null`.
You can still apply the fix one type at a time.

### Does SA1402 flag enums and interfaces?

Not by default. Only classes count unless you add other kinds to `topLevelTypes`.

### Will splitting change behavior?

It should not. The split is a syntax-only move. Using directives and the namespace are
copied into every new file, and the tool never removes a using directive. Build afterward
to confirm.

### Does it need ReSharper or Visual Studio?

No. It is a .NET tool that runs on the .NET 10 SDK, locally or in CI.

### How do I suppress SA1402 instead?

Put this attribute on the type the warning points at. That is the extra type, not the
one that matches the file name:

```csharp
[SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1402:FileMayOnlyContainASingleType", Justification = "Reviewed.")]
public class Square
{
}
```

Or wrap that type in a pragma:

```csharp
#pragma warning disable SA1402 // FileMayOnlyContainASingleType
public class Square
{
}
#pragma warning restore SA1402 // FileMayOnlyContainASingleType
```

## See also

- [README](../README.md): install options, every flag, and the recommended workflow.
- [docs/verification.md](verification.md): how to verify a large split.
- [docs/one-type-per-file-without-resharper.md](one-type-per-file-without-resharper.md):
  one class per file without ReSharper or an IDE.
- [docs/behavior-contracts.md](behavior-contracts.md): what each phase and guard promises.
- [docs/decision-log.md](decision-log.md): why the tool works in three phases and never
  prunes usings.
- [SA1402 rule documentation](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/SA1402.md)
- [StyleCop configuration: maintainability rules](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/Configuration.md#maintainability-rules)
- [dotnet format](https://learn.microsoft.com/dotnet/core/tools/dotnet-format)
