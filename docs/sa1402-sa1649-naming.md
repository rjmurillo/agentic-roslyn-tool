# SA1402 and SA1649 together: what to name a file when two types share a name

SA1649 checks the part of the file name before the first dot against the first type in
the file. For a generic `Foo<T>`, the default `stylecop` convention accepts `Foo{T}.cs` or
`Foo.cs`, while the `metadata` convention requires ``Foo`1.cs``. So `Foo` and `Foo<T>` can
live in `Foo.cs` and `Foo{T}.cs` without breaking SA1402 or SA1649.

This page explains the rules behind that answer, what `agentic-roslyn-tool` names each
file when it splits one, and what to do in the cases it cannot name for you.

Every result below comes from a real build with StyleCop.Analyzers `1.2.0-beta.556` on
.NET SDK 10.0.401, and from running the tool against a sample repository.

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

The plan phase writes this manifest row. Paths are shortened here; the tool writes absolute
paths.

```csv
originalPath,keptPath,gitMove,status,reason,note,newFilePath,type
"src/Result.cs","src/Result.cs",false,"split","","","src/Result{T}.cs","Result<T>"
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
`Foo.cs`. The tool does not read your StyleCop settings. Every generic file it emits,
whether `Foo{T}.cs` or a plain `Foo.cs`, raises SA1649 in a `metadata` repository, unless
its first type is `partial`.

You have two choices:

* Rename each generic file by hand after the content phase, for example with
  ``git mv 'Result{T}.cs' 'Result`1.cs'``. The single quotes keep the backtick literal.
  Commit the renames on their own so Git records them as renames.
* Switch the repository to the `stylecop` convention, if nothing else depends on
  `metadata`. See [Configure the convention](#configure-the-convention).

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
outputs as `<OriginalFileName>.<TypeName>.cs`. The plan says so in the `note` column:

```csv
originalPath,keptPath,gitMove,status,reason,note,newFilePath,type
"src/A.cs","src/A.cs",false,"split","","resolved output path collision: src/A.cs -> src/A.Shared.cs; src/B.cs -> src/B.Shared.cs","src/A.Shared.cs","Shared"
"src/B.cs","src/B.cs",false,"split","","resolved output path collision: src/A.cs -> src/A.Shared.cs; src/B.cs -> src/B.Shared.cs","src/B.Shared.cs","Shared"
```

The collision check ignores case, so `Shared.cs` and `shared.cs` count as the same path.
If the qualified names collide as well, both inputs are skipped with
`qualified output path collision requires manual handling`.

SA1402 is now satisfied, but SA1649 flags both new files. It reads `A.Shared.cs` as `A`,
and the first type in that file is `Shared`. Pick one of these fixes:

* **Rename by hand to `Shared.A.cs` and `Shared.B.cs`.** SA1649 reads both as `Shared`, so
  they pass. This is the best choice when only a few files collided. The tool does not
  produce this form; it is a manual step after the content phase.
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
`Box<T>` and `Box<U>` in two namespace blocks of one file become `Box{T}.cs` and
`Box{U}.cs`, and the split goes ahead. Both names pass SA1649 under `stylecop`.

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
Under `metadata` it wants ``Foo`1.cs``.

**Does SA1649 ignore suffixes like `.Designer.cs`?**
Yes. It compares only the part of the file name before the first dot.

**Why does `A.Shared.cs` raise SA1649?**
SA1649 reads the name as `A`, which does not match the type `Shared`. `Shared.A.cs` passes.

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

* [README: Known limitations](../README.md#known-limitations)
* [Behavior contracts: BuildPlan](behavior-contracts.md#buildplan), which lists every skip
  reason the planner can return.
* [Stack Overflow: StyleCop SA1402 and Generics](https://stackoverflow.com/questions/18549283/stylecop-sa1402-and-generics),
  a long-standing question about keeping `Operation` and its generic versions together.
