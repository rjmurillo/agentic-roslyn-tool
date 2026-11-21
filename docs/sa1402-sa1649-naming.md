# SA1402 and SA1649 together: what to name a file when two types share a name

SA1649 checks the part of the file name before the first dot against the first type in
the file. For a generic `Foo<T>`, the default `stylecop` convention accepts `Foo{T}.cs` or
`Foo.cs`, while the `metadata` convention requires ``Foo`1.cs``. So `Foo` and `Foo<T>` can
live in `Foo.cs` and `Foo{T}.cs` without breaking SA1402 or SA1649.

This page explains the rules behind that answer, what `agentic-roslyn-tool` names each
file when it splits one, and what to do in the cases it cannot name for you.

Every result below comes from a real build with StyleCop.Analyzers `1.2.0-beta.556`, and
from running the tool against a sample repository. The build ran on .NET SDK 10.0.401,
except for the metadata section, which names its own.

## What SA1649 actually compares

[SA1649](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/SA1649.md)
works in four steps, read from its source:

1. It takes the file name and keeps only the text before the first dot. `Form1.Designer.cs`
   is read as `Form1`, and `A.Shared.cs` is read as `A`.
2. It picks the first type in the file. A class, struct, record, or interface wins over an
   enum or a delegate, even when the enum comes first. An enum or a delegate is checked only
   when it is the only type in the file.
3. If that first type is `partial`, it stops and reports nothing.
4. It compares the two names without regard to case.

For a generic type, the expected name depends on the `fileNamingConvention` setting.
These results come from one build per convention:

| File name | Declares | `stylecop` (default) | `metadata` |
|---|---|---|---|
| `Widget.cs` | `class Widget` | pass | pass |
| `gadget.cs` | `class Gadget` | pass | pass |
| `Form1.Designer.cs` | `class Form1` | pass | pass |
| `Box{T}.cs` | `class Box<T>` | pass | SA1649 |
| `Pair{TKey,TValue}.cs` | `class Pair<TKey, TValue>` | pass | SA1649 |
| `Crate.cs` | `class Crate<T>` | pass | SA1649 |
| ``Bin`1.cs`` | `class Bin<T>` | SA1649 | pass |
| `Crate{T}.cs` | `class Crate` (not generic) | SA1649 | SA1649 |
| `A.Shared.cs` | `class Shared` | SA1649 | SA1649 |
| `Shared.B.cs` | `class Shared` | pass | pass |
| `Mismatch.cs` | `partial class Thing` | pass | pass |
| `Mismatch2.cs` | `class Thing` | SA1649 | SA1649 |
| `Colors.cs` | `enum Color` only | SA1649 | SA1649 |
| `TwoEnums.cs` | `enum Shape` and `enum Size` | pass | pass |
| `Mode.cs` | `enum Mode`, then `class Engine` | SA1649 | SA1649 |

Two rows are easy to misread. `Crate{T}.cs` fails because the braces belong only to a
generic type, so the non-generic `Crate` must not live there. `Mode.cs` fails because the
rule checks the class `Engine`, not the enum that comes first.

## `Foo` and `Foo<T>` in one file

SA1402 treats `Foo` and `Foo<T>` as two different types. A file that holds both raises
SA1402 on the second one. The fix is two files: `Foo.cs` for the non-generic type and
`Foo{T}.cs` for the generic one.

`agentic-roslyn-tool` does this for you. It starts from this file:

```csharp
namespace Demo;

internal class Result
{
    public bool Succeeded { get; init; }
}

internal class Result<T> : Result
{
    public T? Value { get; init; }
}
```

The plan phase writes this manifest row. Paths and hashes are shortened here; the tool
writes absolute paths and a full 64 digit `contentHash`.

```csv
originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash
"src/Result.cs","src/Result.cs",false,"split","","","src/Result{T}.cs","Result<T>","sha256:9c1e..."
```

After the content phase, `Result.cs` keeps `Result` and `Result{T}.cs` holds `Result<T>`.
The build that raised SA1402 on `Result.cs` before the split raises nothing on either
file after it.

The rules the tool follows:

* The type whose name matches the file stays in place. When both the generic and the
  non-generic type match, the non-generic one stays.
* The braces appear only when two types in the same input share a simple name. The type
  parameter names go inside, separated by commas, which is the same form the `stylecop`
  convention expects. `Foo<TKey, TValue>` becomes `Foo{TKey,TValue}.cs`.
* A generic type with no same-name sibling gets the plain name. A lone `Foo<T>` becomes
  `Foo.cs`, which the `stylecop` convention accepts.
* The tool never writes over an existing file. If `Foo{T}.cs` already exists, the input is
  skipped with `target path already exists`.

## If you use the metadata convention

Under `metadata`, SA1649 expects ``Foo`1.cs`` for `Foo<T>` and rejects both `Foo{T}.cs` and
`Foo.cs`. The number is the count of type parameters, so `Pair<TKey, TValue>` is
``Pair`2.cs``. A type that is not generic keeps its plain name, `Foo.cs`, under both
conventions. A generic delegate follows the same rule as a generic class.

The results in this section come from a separate build: StyleCop.Analyzers
`1.2.0-beta.556` on .NET SDK 11.0.100, with `fileNamingConvention` set to `metadata`. It
accepted ``Foo`1.cs``, ``Pair`2.cs``, a generic delegate in ``Make`1.cs``, and a plain
`Plain.cs`. It reported SA1649 for `Foo<T>` in `Foo.cs`, for `Foo<T>` in `Foo{T}.cs`, for a
generic delegate in a plain named file, and for ``A.Shared`1.cs``. It accepted
``Shared`1.A.cs``. The delegate rule
depends on the StyleCop version. The `1.1.118` source has no delegate case in
`FileNameHelpers`, so check a generic delegate against the version you build with.

The tool does not read your StyleCop settings. Tell it with `--file-naming metadata`, on
each of the three runs:

1. `agentic-roslyn-tool split-types --input files.txt --manifest plan.csv --dry-run --file-naming metadata`
2. `agentic-roslyn-tool split-types --input files.txt --manifest plan.csv --phase renames --file-naming metadata`
3. `agentic-roslyn-tool split-types --input files.txt --manifest plan.csv --phase content --file-naming metadata`

Pass the flag to all three phases. It decides file names, so each phase must agree. The
content phase skips a file whose manifest row the other convention would have planned
differently and writes nothing for it. The reason is
`content phase plan does not match manifest`. The row's note names both flag values, the
manifest path, and the path this run computes. For a kept file the two conventions name
differently, the note starts ``manifest keeps Foo`1.cs, this run keeps Foo.cs``, with full
paths, and then says what to run next. A file whose paths come out the same under both
conventions still splits. That covers every file with no generic type. The run exits 0
and the plan is unchanged, so run the content phase again with the flag the plan was made
with.

The renames phase checks less. It recomputes the plan and holds each file to the manifest's
hash and status. It runs a rename only when the plan holds that same rename, so under the
wrong value it moves nothing the plan did not ask for. It can still leave out a rename the
plan did ask for: a renames run without the flag over a `metadata` plan skips
the rename to ``Foo`1.cs`` and reports success. The content phase then fails that file and
says to run the renames phase. Run it with the flag and continue.

If you decide to change the flag, plan again. After a rename has landed that needs one more
step. A list file still names the old path, and the tool reports `input file does not
exist` for it. Undo the rename with `git mv`, or put the file's current path in the list,
then plan.

With the flag, the rules in the section above change in four ways:

* Every generic type gets the arity name, with or without a same-name sibling. The
  `Result.cs` example above yields `Result.cs` and ``Result`1.cs``.
* The type that stays in place is the one whose arity name matches the file. In
  ``Foo`1.cs``, `Foo<T>` stays and a plain `Foo` moves to `Foo.cs`.
* A generic type kept in a plain named file is renamed. `Foo.cs` holding `Foo<T>` and `Bar`
  becomes ``Foo`1.cs`` in the renames phase, and `Bar` moves to `Bar.cs`. When two inputs
  would take the same name, such as `Foo.cs` and `Foo.Extra.cs` each holding a part of
  `Foo<T>`, the first in input order is renamed and the other keeps its name, with a
  `rename collision` note. A directory input is read in sorted order, so `Foo.Extra.cs`
  comes first there. SA1649 skips a file whose first type is `partial`, so that second file
  passes.
* Only arity tells two same-name types apart. `Box<T>` and `Box<U>` both map to
  ``Box`1.cs``, so that file is skipped with `target path collision within split`. The
  `stylecop` convention splits it, as `Box{T}.cs` and `Box{U}.cs`.

Three things the flag does not do:

* It does not rename a file that needs no split. A lone `Foo<T>` in `Foo.cs` is skipped as
  nothing to split, so rename it by hand, for example with
  ``git mv 'Foo.cs' 'Foo`1.cs'``. The single quotes keep the backtick literal.
* It does not fix the default qualified form used for a
  [cross-file collision](#same-type-name-in-two-namespaces). That becomes
  ``A.Shared`1.cs``, which SA1649 reads as `A` and still rejects. Add
  `--collision-naming type-first` to the plan and the name becomes ``Shared`1.A.cs``,
  which SA1649 reads as ``Shared`1`` and accepts.
* It does not change your StyleCop settings. If nothing depends on `metadata`, switching
  the repository to `stylecop` also works. See
  [Configure the convention](#configure-the-convention).

In a `--json` report the backtick is written as the escape `\u0060`. Any JSON parser
decodes it back to a backtick. The CSV manifest holds the character itself.

## Same type name in two namespaces

SA1402 has no problem with two files that each declare a type called `Shared`, as long as
the namespaces differ. Splitting them is where the trouble starts. Take `A.cs` and `B.cs`
in one folder:

```csharp
namespace Demo.One;

internal class A
{
}

internal class Shared
{
}
```

`B.cs` is the same, with `namespace Demo.Two` and `class B`. Each file wants to emit
`Shared.cs` into the same folder. The tool detects the collision and qualifies both
outputs. By default the form is `<OriginalFileName>.<TypeName>.cs`. The plan says so in
the `note` column:

```csv
originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash
"src/A.cs","src/A.cs",false,"split","","resolved output path collision: src/A.cs -> src/A.Shared.cs; src/B.cs -> src/B.Shared.cs","src/A.Shared.cs","Shared","sha256:41d7..."
"src/B.cs","src/B.cs",false,"split","","resolved output path collision: src/A.cs -> src/A.Shared.cs; src/B.cs -> src/B.Shared.cs","src/B.Shared.cs","Shared","sha256:b94d..."
```

The collision check ignores case, so `Shared.cs` and `shared.cs` count as the same path.
If the qualified names collide as well, both inputs are skipped with
`qualified output path collision requires manual handling`.

SA1402 is now satisfied, but SA1649 flags both new files. It reads `A.Shared.cs` as `A`,
and the first type in that file is `Shared`.

### Let the tool pick a name SA1649 accepts

Pass `--collision-naming type-first` and the tool swaps the two parts of the qualified
name. It emits `<TypeName>.<OriginalFileName>.cs`:

```csv
originalPath,keptPath,gitMove,status,reason,note,newFilePath,type,contentHash,collisionNaming
"src/A.cs","src/A.cs",false,"split","","resolved output path collision: src/A.cs -> src/Shared.A.cs; src/B.cs -> src/Shared.B.cs","src/Shared.A.cs","Shared","sha256:41d7...","type-first"
"src/B.cs","src/B.cs",false,"split","","resolved output path collision: src/A.cs -> src/Shared.A.cs; src/B.cs -> src/Shared.B.cs","src/Shared.B.cs","Shared","sha256:b94d...","type-first"
```

SA1649 reads both names as `Shared`, so both files pass. The flag is opt-in, and the
default stays `file-first`, so a run without the flag behaves as it did before. What
to know:

* **Pass the flag to the plan phase only.** The plan records the value in the manifest's
  `collisionNaming` column. The renames and content phases read it from there when the flag
  is absent. Passing the same value again is harmless. Version 0.1.1 has no column, so
  there you must pass the flag to every phase.
* **A different value on a later phase stops the run.** Renames or content given a value
  that contradicts the manifest exits 3 before touching any file, with an `error:` line
  naming both values. Omit the flag, or re-run the plan with the value you want.
* **Do not edit the `collisionNaming` column.** A blank, unknown, or mixed value exits 3.
  A column edited to the other value, with paths and notes left alone, reaches the per-row
  check below.
* **The per-row check reads the plan's own note.** The content phase refuses a path only
  when the `note` column records that the plan chose it under the other value. That
  happens only after the column edit described above. A path you typed into the manifest
  by hand is still accepted when it is free, whatever its shape. Leave the note alone when
  you edit a path. A row with no note is not checked, and a row whose note you rewrote to
  name a type-first path is refused under `file-first`.
* **Some source names are refused.** Roslyn treats a file as generated code when its name
  ends in `.g`, `.g.i`, `.designer`, or `.generated` before the extension. Analyzers skip
  such a file and nullable annotations switch off in it. Type-first puts the source file
  name last, so `G.cs` or `Designer.cs` would yield `Shared.G.cs` or
  `Shared.Designer.cs`. The tool skips that input with
  `qualified output name would be read as generated code; manual handling required`. The
  `note` column names the refused path.
* **A generic type keeps its braces.** When `Shared<T>` collides too, the output is
  `Shared{T}.A.cs`. SA1649 reads that as `Shared{T}`. It passes under the `stylecop`
  convention and raises SA1649 under `metadata`, the same as `Shared{T}.cs` would. A build
  with StyleCop.Analyzers `1.2.0-beta.556` on .NET SDK 10.0.404 confirmed both results.
* **The tool still never writes over a file.** If `Shared.A.cs` already exists, the
  content phase skips that input with `target path already exists`. If two inputs get the
  same qualified name, both are skipped with
  `qualified output path collision requires manual handling`.
* **The tool does not read your StyleCop settings.** It cannot tell that a repository
  enforces SA1649, so you choose the form on the command line.

### Fix files that were already split

If you ran the split with the default naming, pick one of these fixes:

* **Rename by hand to `Shared.A.cs` and `Shared.B.cs`.** SA1649 reads both as `Shared`, so
  they pass. This is the best choice when only a few files collided. It is the same form
  `--collision-naming type-first` produces.
* **Move each type into a folder named for its namespace**, such as `One/Shared.cs` and
  `Two/Shared.cs`. Both pass SA1649. The tool does not move files into folders, so this is
  manual too.
* **Suppress SA1649 for just those files.** A section in `.editorconfig` does it without
  touching the source:

  ```ini
  [src/{A,B}.Shared.cs]
  dotnet_diagnostic.SA1649.severity = none
  ```

  This is better than turning SA1649 off for the whole repository.

## Two types in one file that map to one file name

The tool builds generic file names from the type parameter names, not from the arity.
Take `Box<T>` and `Box<U>` in two namespace blocks of one file. In `Box.cs`, `Box<T>`
stays in `Box.cs` and `Box<U>` moves to `Box{U}.cs`. In a file with another name, such as
`Holder.cs`, the file is renamed to `Box{T}.cs` and `Box<U>` moves to `Box{U}.cs`. Either
way the split goes ahead, and every name passes SA1649 under `stylecop`.

When two types in one file map to the same file name, no name can tell them apart. That
happens for two non-generic types with the same name, or two generic types with the same
type parameter names, such as `Bag<T>` twice. The tool refuses to split the file and
records it as skipped with `target path collision within split: <path>`.

That includes two parts of one `partial` type in one file:

```csharp
namespace Demo;

internal partial class Order
{
    public int Id { get; init; }
}

internal partial class Order
{
    public string? Customer { get; init; }
}
```

```csv
"src/Order.cs","src/Order.cs",false,"skipped","target path collision within split: src/Order.cs","","",""
```

This file needs no split.
[SA1402](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/SA1402.md)
allows several parts of one partial type in one file, and the build raised no SA1402 on
`Order.cs`. Any other pair that maps to one file name, such as two `Shared` classes in two
namespace blocks of one file, needs a manual split.

## A `file` type in the file

The tool never renames a file that declares a `file` type. The compiler builds that type's
metadata name from the file's name, so a rename changes it. In one build,
`typeof(AlphaHelper).FullName` was `Probe.<Alpha>F6987AE8...__AlphaHelper` in `Alpha.cs`,
and `Probe.<AlphaRenamed>FD638230...__AlphaHelper` after the file became
`AlphaRenamed.cs`. So two SA1649 cases stay as they were before the split. Both were
measured with StyleCop.Analyzers `1.2.0-beta.556`.

* **The `file` type is declared first.** SA1649 compares the file name with the first
  type. With `file class AlphaHelper` above `internal class Alpha` in `Alpha.cs`, the build
  reported SA1649 at line 3. The tool does not reorder types. Move the `file` type below
  the ordinary one by hand: with `Alpha` first, the same build raised no SA1649.
* **No type is named for the file.** `Widgets.cs` held `file class WidgetHelper` and
  `internal class Gadget`, and the build reported SA1649. The tool skips that file, since
  one ordinary type leaves nothing to move. With a second ordinary type it splits. Without
  a `file` type it would also rename the file for its kept type. Here it keeps
  `Widgets.cs`, and the split row's `note` ends with `the file keeps its name because a rename changes a file-local type's
  metadata name`. The same holds under `--file-naming metadata`: `Foo<T>` beside a `file`
  type stays in `Foo.cs`, where the convention expects ``Foo`1.cs``.

Rename such a file by hand only when nothing reads the `file` type's metadata name.
Reflection, logs, and serialized type names can all read it. Otherwise suppress SA1649
for that file, with the same `.editorconfig` section shown
[above](#fix-files-that-were-already-split):

```ini
[src/Widgets.cs]
dotnet_diagnostic.SA1649.severity = none
```

SA1402 also stays on these files. See
[fix-sa1402-whole-repo.md](fix-sa1402-whole-repo.md#files-that-hold-a-file-type).

## Configure the convention

`stylecop` is the default, so you only need a setting to choose `metadata`, or to make the
default explicit. In `stylecop.json`:

```json
{ "settings": { "documentationRules": { "fileNamingConvention": "stylecop" } } }
```

The project must include the file as an analyzer additional file:

```xml
<ItemGroup>
  <AdditionalFiles Include="stylecop.json" />
</ItemGroup>
```

The [configuration reference](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/Configuration.md#file-naming-conventions)
also lists an `.editorconfig` key. It worked in the `1.2.0-beta.556` build behind this
page:

```ini
[*.cs]
stylecop.documentation.fileNamingConvention = metadata
```

## FAQ

**What file name does StyleCop want for a generic class?**
`Foo{T}.cs` under the default `stylecop` convention, and plain `Foo.cs` is accepted too.
Under `metadata` it wants ``Foo`1.cs``. The tool plans that name when you pass
`--file-naming metadata`.

**Does SA1649 ignore suffixes like `.Designer.cs`?**
Yes. It compares only the part of the file name before the first dot.

**Why does `A.Shared.cs` raise SA1649?**
SA1649 reads the name as `A`, which does not match the type `Shared`. `Shared.A.cs` passes.
Pass `--collision-naming type-first` to make the tool emit that form.

**Does SA1649 check partial classes?**
No. When the first type in the file is `partial`, the rule reports nothing.

**Can `dotnet format` fix SA1649 in bulk?**
No. The SA1649 code fix has no Fix All provider. A run of
`dotnet format analyzers --diagnostics SA1649 --severity warn` changes no file and logs:

```text
Unable to fix SA1649. Code fix SA1649CodeFixProvider doesn't support Fix All in Solution.
```

SA1402 has the same gap, which is why this tool exists.

**Can two parts of one partial class stay in one file under SA1402?**
Yes. SA1402 allows several parts of one partial type in one file.

## See also

* [One class per file without ReSharper](one-type-per-file-without-resharper.md), the
  step by step guide to running the split.
* [Fix SA1402 across a whole repository](fix-sa1402-whole-repo.md), why `dotnet format`
  cannot fix SA1402 and the three step workflow.
* [README: Known limitations](../README.md#known-limitations)
* [Behavior contracts: BuildPlan](behavior-contracts.md#buildplan), which lists every skip
  reason the planner can return.
* [Stack Overflow: StyleCop SA1402 and Generics](https://stackoverflow.com/questions/18549283/stylecop-sa1402-and-generics),
  a long-standing question about keeping `Operation` and its generic versions together.
