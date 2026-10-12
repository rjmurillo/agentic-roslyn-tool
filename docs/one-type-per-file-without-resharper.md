# One class per file without ReSharper: a free CLI that moves every C# type into its own file

To move every C# class into its own file without ReSharper, install the free
AgenticRoslynTool .NET tool and run
`agentic-roslyn-tool split-types --input src --dry-run` to see the plan. Then run the
`renames` and `content` phases. Each top-level type lands in its own file, named for the
type, with its comments, attributes, usings, and namespace.

The tool is MIT licensed, needs the .NET 10 SDK, and needs no IDE. It reads C# with the
Roslyn Syntax API, so it does not load MSBuild or build a semantic model.

## Options compared

Three other tools can do this job. Each one runs inside an IDE.

| Tool | Cost | Where it runs | Many types at once | How you choose what moves |
| --- | --- | --- | --- | --- |
| [ReSharper, Move Types Into Matching Files](https://www.jetbrains.com/help/resharper/Move_classes_into_separate_files_in_one_go.html) | Paid subscription, free for non-commercial use | IDE extension | Yes, for a project | A dialog lists the types |
| [CodeRush, Move All Types to Files](https://docs.devexpress.com/CodeRushForRoslyn/403390) | Free | Visual Studio extension | Yes, for a file, folder, or project | A "Types to move" window |
| [Visual Studio, Move type to matching file](https://learn.microsoft.com/visualstudio/ide/reference/refactoring-rename-move#move-type-to-matching-file) | Included with Visual Studio | Visual Studio | No, one type per action | Place the cursor on the type |
| AgenticRoslynTool `split-types` | Free, MIT license | Command line, CI, or an AI agent | Yes, for any set of files | A CSV or JSON plan you review before any file changes |

Pick an IDE refactoring when you already work in that IDE. Pick the
command line tool when you work in VS Code or on Linux, when you have no ReSharper
license, or when CI or an AI agent has to run the change.

## Install

Pick one of three ways. Each needs the .NET 10 SDK.

Install it globally:

```bash
dotnet tool install --global AgenticRoslynTool
agentic-roslyn-tool split-types --help
```

Install it into a repository so every contributor gets the same version:

```bash
dotnet new tool-manifest        # only if the repository has no manifest yet
dotnet tool install AgenticRoslynTool
dotnet agentic-roslyn-tool split-types --help
```

Run it once without installing:

```bash
dnx AgenticRoslynTool -- split-types --help
```

The `--` matters. Without it, `dnx` reads options such as `--help` as its own.

The commands below use `agentic-roslyn-tool`. Substitute `dotnet agentic-roslyn-tool` or
`dnx AgenticRoslynTool --` if you picked one of the other two.

## Preview, rename, split

Run these from the repository root. The work has three phases. The `plan` phase writes
only a manifest. The `renames` phase runs `git mv`. The `content` phase writes the new
files.

For a quick look, point the tool at a directory:

```bash
agentic-roslyn-tool split-types --input src --dry-run
```

`--dry-run` is the `plan` phase. It prints the plan as CSV and changes no source file.
It does write the manifest, by default to `sa1402-split-manifest.csv` in the current
directory, so delete that file afterward or pass `--manifest` with a path outside the
repository.

For the real run, capture the file list once and reuse it for all three phases. A
directory scan after the `renames` phase finds the new file names, which the plan does not
know, so the `content` phase would skip them. Keep the list and the manifest outside the
repository so `git add -A` does not commit them. The `core.quotePath=false` setting stops
git from escaping non-ASCII file names, which the tool would then fail to find.

In Bash:

```bash
git -c core.quotePath=false ls-files '*.cs' > ../files.txt

agentic-roslyn-tool split-types --input ../files.txt --manifest ../split-plan.csv --dry-run

agentic-roslyn-tool split-types --phase renames --input ../files.txt --manifest ../split-plan.csv
git add -A
git commit -m "Rename C# files to match primary types"

agentic-roslyn-tool split-types --phase content --input ../files.txt --manifest ../split-plan.csv
dotnet build
```

In PowerShell:

```powershell
git -c core.quotePath=false ls-files '*.cs' | Set-Content ../files.txt

agentic-roslyn-tool split-types --input ../files.txt --manifest ../split-plan.csv --dry-run

agentic-roslyn-tool split-types --phase renames --input ../files.txt --manifest ../split-plan.csv
git add -A
git commit -m "Rename C# files to match primary types"

agentic-roslyn-tool split-types --phase content --input ../files.txt --manifest ../split-plan.csv
dotnet build
```

Review the plan before you go on. Commit the renames on their own, so git records each
one as a rename and the file keeps its history. Review the diff after the `content` phase
and build to confirm the result.

## What you get

This sample project has one file, `src/Models.cs`, with three types:

```csharp
using System.Collections.Generic;

namespace Shop;

/// <summary>A customer order.</summary>
public class Order
{
    public List<OrderLine> Lines { get; } = new();

    public OrderStatus Status { get; set; }
}

public enum OrderStatus
{
    Open,
    Shipped,
}

public record OrderLine(string Sku, int Quantity);
```

The plan phase prints this manifest. The tool writes absolute paths; they are shortened
here:

```text
originalPath,keptPath,gitMove,status,reason,note,newFilePath,type
"src/Models.cs","src/Order.cs",true,"split","","","src/OrderStatus.cs","OrderStatus"
"src/Models.cs","src/Order.cs",true,"split","","","src/OrderLine.cs","OrderLine"
```

Standard error carries the summary line:

```text
plan: 1 input(s), 1 split, 0 skipped, 0 failed, 2 new file(s).
```

No type is named `Models`, so the tool keeps the first type, `Order`, and the `renames`
phase moves `Models.cs` to `Order.cs`. The `content` phase then leaves three files:

`src/Order.cs`:

```csharp
using System.Collections.Generic;

namespace Shop;

/// <summary>A customer order.</summary>
public class Order
{
    public List<OrderLine> Lines { get; } = new();

    public OrderStatus Status { get; set; }
}
```

`src/OrderStatus.cs`:

```csharp
using System.Collections.Generic;

namespace Shop;

public enum OrderStatus
{
    Open,
    Shipped,
}
```

`src/OrderLine.cs`:

```csharp
using System.Collections.Generic;

namespace Shop;

public record OrderLine(string Sku, int Quantity);
```

Each file gets every using directive from the source, even one it does not need. The tool
never removes a using, because a using that looks unused can still supply an extension
method. The project built with 0 warnings and 0 errors after the split.

These rules decide what goes where:

- **What moves.** Every top-level class, struct, record, interface, enum, and delegate.
  A top-level type sits at the root of the file or directly inside a namespace.
- **Which type stays.** The type whose name matches the file name, compared without case.
  A non-generic type wins over a generic one. When no type matches, the first type stays
  and the file is renamed to it, unless a file with that name already exists.
- **File names.** Each type goes to `TypeName.cs`. A lone generic type `Foo<T>` goes to
  `Foo.cs`. When several types in one file share a name and differ by generic arity, the
  generic ones get their type parameters in braces, such as `Result{T}.cs` and
  `Result{TValue,TError}.cs`.
- **Name clashes across files.** When two input files would create the same file name,
  the tool usually names both `FileName.TypeName.cs`. That name breaks StyleCop SA1649, so
  those files need a decision from you afterward.
- **What travels with a type.** Its XML doc comments, attributes, and ordinary comments.

Enums, interfaces, and delegates move too. Some teams keep those next to the class that
uses them. If yours does, leave those files out of the input list.

## What it leaves alone

The tool skips a file and records the reason in the manifest when it cannot split the
file safely. The common cases:

- The file has top-level statements.
- The file has a `file`-local type.
- An `#if` or `#region` block spans more than one type, so a split would break it apart.
- An `#if` or `#region` has no matching end directive.
- A split would leave an empty `#if` or `#region` block behind.
- Two types in the file share a name and generic arity. Two parts of one `partial` type
  in the same file count as this case, so that file is skipped.
- A file the split would create already exists.
- Two input files would create the same file name, even after the `FileName.TypeName.cs`
  fallback.
- The file is not valid UTF-8 and has no byte order mark.
- The file has one type or none, so there is nothing to split.

Nested types stay inside their parent type. Only top-level types move.

A file with syntax errors is marked `failed`, not skipped, and is not written. The tool
has no special handling for `partial` types. A `partial` part next to a different type
is split like any other type.

[docs/behavior-contracts.md](behavior-contracts.md) lists every skip and failure reason.

## Running it in CI or from an AI agent

Add `--json` to get one JSON report on standard output instead of CSV. The manifest path,
the summary line, and every error go to standard error, so standard output stays
parseable. The report leads with counts:

```json
"summary": {
  "total": 1,
  "split": 1,
  "skipped": 0,
  "failed": 0,
  "newFiles": 2
}
```

The exit code tells a caller what happened without parsing anything. `0` means the run
completed and nothing failed. `1` means at least one file failed. `2` means the command
line was wrong. `3` means the run could not start or finish. A run that skips every file
still exits `0`, so read `summary` to learn whether work happened.

## Enforce it afterward

Once each file holds one type, turn on StyleCop rule
[SA1402](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/SA1402.md)
so new files stay that way. By default that rule still lets enums, structs, interfaces,
and delegates sit next to a class.

## FAQ

**Can Visual Studio move all classes to separate files at once?**
Not on its own. Its Move type to matching file quick action moves the type under the
cursor. ReSharper, CodeRush, or a command line tool can move many at once.

**Is there a free alternative to ReSharper's Move Types Into Matching Files?**
Yes. CodeRush is a free Visual Studio extension, and AgenticRoslynTool is a free command
line tool that needs no IDE.

**Does it work in VS Code or on Linux?**
Yes. It is a .NET tool, so it needs the .NET 10 SDK, not an IDE. The sample on this page
was run on Linux.

**What about nested classes?**
They stay inside their parent type. Only top-level types move.

**Is this the same as splitting one class into multiple files?**
No. That is what `partial` classes do. This tool moves separate types apart and never
splits one type.

**Will it break my build?**
It is a syntax-only move that copies every using and the namespace into each new file. Build
after the `content` phase to confirm.

**Does it rename files?**
Yes, in the separate `renames` phase, through `git mv`, so git records the renames.

## See also

- [README](../README.md): every option, the full workflow, and known limitations.
- [docs/behavior-contracts.md](behavior-contracts.md): what each phase and each guard
  promises.
- [AgenticRoslynTool on NuGet](https://www.nuget.org/packages/AgenticRoslynTool)
- [MIT license](../LICENSE)
