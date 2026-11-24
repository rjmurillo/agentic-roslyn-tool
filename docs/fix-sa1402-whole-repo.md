# Fix SA1402 across a whole repository: why dotnet format does nothing and how to split every file

<!-- doc-accuracy: external-sdk StyleCop.Analyzers -- the C# samples are user code and StyleCop attributes, not this tool's own types -->

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

One case never triggers the rule. Parts of the same partial type may share a file. A type
declared with the `file` modifier is not exempt: in a build with StyleCop.Analyzers
`1.2.0-beta.556`, a `file class` beside another class raised SA1402. See
[Files that hold a `file` type](#files-that-hold-a-file-type).

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
> Versions after 0.1.1 run the `plan` phase when `--phase` is missing, and change no
> source file. Version 0.1.1 and earlier run `content` instead, which rewrites source.
> Pass `--dry-run` or `--phase` every time, so a script means the same on any version.

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

Feed every phase the same list file. After the renames phase, `Shapes.cs` is gone and
`Circle.cs` exists. The list file still names `Shapes.cs`, and the manifest tells the
content phase where that file went. A directory input works too: the content phase finds
`Circle.cs` and maps it back to the plan row for `Shapes.cs` through its planned kept path.

Only the plan phase writes the manifest at `--manifest`. The renames and content phases
leave it exactly as you reviewed it, and record what they did in a report file beside it,
`../split-types.report.csv` here. Pass `--report` to put it somewhere else. Do not point
the content phase at a new, empty manifest path: it needs the plan.

If a content run fails a file, fix the cause and run the same content command again.
Never edit the source file to get past the tool. A failed check on a file your build
compiles is a bug in the tool, so exclude that file and report it. The plan still lists
that file as `split`. Files the first run already split are reported as
`skipped` with a reason that starts with `already applied`.

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
      "newFiles": [{ "newFilePath": "/repo/src/Shapes/Square.cs", "type": "Square" }],
      "contentHash": "sha256:5f2b0e9c41d7a8366c1e0b94d2f7a3158e6c0d4b9a7f31e2c58d06b4a19e73fc"
    }
  ]
}
```

The full document also carries `manifest`, the path of the plan manifest, and `report`,
the path of the report file a renames or content run wrote. A one-line
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
| File declares a `file` type and one other type | skipped | `nothing to split: file-local types stay in their file and at most one other type is declared` |
| A type that would move names a `file` type | skipped | `moved type names a file-local type; manual split required` |
| A `file` type declares extension members | skipped | `file-local type declares extension members; manual split required` |
| A using directive names a `file` type | skipped | `using directive names a file-local type; manual split required` |
| A type lists a `file` type among its base types | skipped | `type lists a file-local type among its base types; manual split required` |
| A `file` type shares the file with a `global using` directive | skipped | `file-local type shares the file with a global using directive; manual split required` |
| A `file` type shares the file with a `#line`, `#nullable`, `#pragma`, `#define`, or `#undef` directive on or inside a type, or on an assembly attribute | skipped | `file-local type shares the file with a #line, #nullable, #pragma, #define, or #undef directive on or inside a type or on an assembly attribute; manual split required` |
| A `file` type shares the file with an excluded `#if` branch | skipped | `file-local type shares the file with conditionally excluded text; manual split required` |
| An `#if` or `#region` spans two types | skipped | `<kind> group at line <n> crosses top-level type boundaries` |
| Two types in one file share a name and type parameter list | skipped | `target path collision within split: <path>` |
| File already has one type | skipped | `nothing to split: input has 1 top-level type declaration(s)` |
| Path matches `--exclude` | skipped | `excluded by pattern: <pattern>` |
| File does not parse | failed | `input has syntax errors: <diagnostics>` |
| File parses only with the preprocessor symbols its `#if` lines name | skipped | `parses only with the preprocessor symbols its #if lines name, which this tool does not define; manual split required` |
| File is a .NET 10 file-based program (`#:` or `#!` lines) | skipped | `file-based program (#: or #! directive): it compiles alone, so no type can move out of it` |

A `dotnet new` template folder holds files that are not C# until the template engine runs,
such as `#if (UseXunit)`. They fail with `input has syntax errors`. Pass
`--exclude <template folder>` to leave them out. A `failed` row on a file your build
compiles is a bug in the tool. Do not edit the file to get past it. Exclude it and
[open an issue](https://github.com/rjmurillo/agentic-roslyn-tool/issues).

The same-name row covers partial types, generic or not. SA1402 allows two parts of one
partial class in a file, but the tool sees two types that want the same file name and
skips it. That is harmless, since SA1402 does not flag that file. Two parts of
`partial class Q<T>` in `Q.cs` are skipped the same way. The tool does not merge or move
partial parts.

Two types with the same name are split when their type parameter lists differ. A generic
type gets its type parameter names in braces, unless it stays in a file already named for
it. So `Box` and `Box<T>` become `Box.cs` and `Box{T}.cs`.

## Files that hold a `file` type

A type declared with the `file` modifier cannot move to its own file. It is visible only
in the file that declares it, and the compiler builds its metadata name from that file's
name. So the tool leaves every `file` type where it is and never renames that file. It
still moves the ordinary types out, when none of them uses a `file` type.

Take `Alpha.cs`:

```csharp
namespace Probe;

internal class Alpha
{
    public static string HelperName() => typeof(AlphaHelper).FullName!;
}

file class AlphaHelper
{
}

internal class Sibling
{
}
```

The plan row is a split, and its `note` says what stays:

```csv
originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash,collisionNaming
"Alpha.cs","Alpha.cs",false,"split","","file-local type stayed in this file: AlphaHelper; SA1402 still flags the file","Sibling.cs","Sibling","sha256:39f75a1df2a6add50368b5c947ee8b17b25ae63df86a9a56e967515230846f43","file-first"
```

`Sibling` moves to `Sibling.cs`. `Alpha` and `AlphaHelper` stay in `Alpha.cs`.

**One SA1402 warning stays, and the tool cannot remove it.** A build with
StyleCop.Analyzers `1.2.0-beta.556` reported SA1402
twice before the split, at `AlphaHelper` and at `Sibling`. After the split it reported
once, at `Alpha.cs(8,12)`, the `file` class. When the kept type is a class or record too,
expect one warning for each `file` class or `file` record. A `file` struct, interface, enum, or delegate beside one class raised
none, because SA1402 checks classes and records only by default. The row's `note` follows
that rule. It says `SA1402 still flags the file` when two classes or records stay, and
`SA1402 still flags the file where it checks more than classes` when they do not. Build
first, and suppress only a warning the build reports. There are two ways. Both were run
against that build.

* **A section in `.editorconfig`.** It leaves the source alone, and works wherever the
  warning lands:

  ```ini
  [src/Alpha.cs]
  dotnet_diagnostic.SA1402.severity = none
  ```

* **A pragma around the declaration the warning names.**

  ```csharp
  #pragma warning disable SA1402 // A file-local type cannot move to its own file.
  file class AlphaHelper
  #pragma warning restore SA1402
  {
  }
  ```

  The warning names the `file` type when the file is named for the ordinary type. When no
  type matches the file name and the `file` type comes first, it names the ordinary type.
  Read the line number in the build output before you place the pragma.

Either one is better than turning SA1402 off for the whole repository.

The tool skips the file when it cannot prove the split is safe. It never guesses:

* **A type that would move names a `file` type.** In the example, `Alpha` uses
  `AlphaHelper`, and that is fine because `Alpha` stays. If `Sibling` used it, the row
  would be `skipped`, and its `note` would read `Sibling names AlphaHelper`. The check is
  by name. It also refuses a type that only has a member of the same name, or names the
  type in a `cref`. Rename that member, or split the file by hand.
* **A `file` type declares extension methods or an extension block.** Code can call these
  without naming the class, so no check by name can find the callers.
* **A `using` alias or `using static` names a `file` type.** Every new file repeats the
  using directives, and the new file cannot see the type. The check is by name, so a
  namespace segment that equals the type's name refuses the file too.
* **A type lists a `file` type among its base types.** For example `class Alpha :
  IMarker<AlphaHelper>`. Code in another file can then get an `AlphaHelper` from a generic
  call without writing the name, and a call on it can bind to another method with no
  error. So the tool refuses the file even though `Alpha` would stay.
* **The file has an `#if` branch that is excluded.** The tool cannot read excluded text.
* **The file has a `global using` directive.** Every new file repeats it, and a global
  alias declared in two files does not compile.
* **A `#line`, `#nullable`, `#pragma`, `#define`, or `#undef` directive sits on or inside
  a type, or on an assembly attribute.** It applies to everything below it, and a split
  would not carry it to every new file unchanged. Move a `#line`, `#nullable`, or
  `#pragma` directly above a `using` line or the `namespace` line, where every file gets a
  copy, and plan again. A `#define` or `#undef` must stay first in the file, so put a
  `using` line directly below it. A file with no `using` line and no `namespace` line has
  no such place. Neither does a `#define` or `#undef` inside an `#if`. Moved above a
  `using` line, that group gets the file skipped with `#if group at line 1 crosses
  top-level type boundaries`.
* **The file has only one other type.** There is nothing to move. The SA1402 warning on
  such a file was there before, and you suppress it the same way.

When the kept file would normally be renamed, it keeps its name here, and the note says
so. SA1649 may then report that file. That warning was there before the split too. See
[sa1402-sa1649-naming.md](sa1402-sa1649-naming.md#a-file-type-in-the-file).

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

The tool names each new file after its type. A generic type gets its type parameter names
in braces only when it shares a name with another type in the same file, as in `Name.cs`
and `Name{T}.cs`. If your repository sets the SA1649 `metadata` convention, pass
`--file-naming metadata` to every phase and a generic type is named ``Name`1.cs`` instead.
See [the metadata section](sa1402-sa1649-naming.md#if-you-use-the-metadata-convention)
before you plan. When two source files in the same folder would each create the same new
file, the tool names both `SourceFile.TypeName.cs`. That form conflicts with SA1649 unless
the type is `partial`, since SA1649 reports nothing when the first type in a file is
partial. Fix just those files rather than disabling SA1649 for the whole repository: rename
them by hand to `TypeName.SourceFile.cs`, move each into a folder named for its namespace,
or suppress SA1649 for those paths. Disabling SA1649 remains an option. The tool fixes
SA1402. It does not fix SA1649. [docs/sa1402-sa1649-naming.md](sa1402-sa1649-naming.md#same-type-name-in-two-namespaces)
explains which file names satisfy both rules.

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
