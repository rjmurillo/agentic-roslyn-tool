# Verifying a mechanical refactor: line conservation, trivia ownership and before/after type scans

To verify a mechanical C# refactor, check four things. Every non-whitespace line survives
(line conservation). Each type's doc comment and attributes land exactly once (trivia
ownership). Every output still parses. A Roslyn scan of top-level types is identical before
and after. `split-types` runs the first three, plus a per-file type set check, for each
file before it writes anything. The repository-wide scan is a step you run yourself.

This page is for the reviewer who has to approve a diff that touches hundreds of files. It
explains what each check proves and what it does not. For the exact contract of each guard,
see [behavior-contracts.md](behavior-contracts.md). For why the guards are built this way,
see [decision-log.md](decision-log.md).

## Why text diffs are not enough

A file split is mostly moves. One type leaves a file and lands in a new one, unchanged. A
line-by-line diff shows that as a block of deletions in one file and a block of additions
in another. Across a few hundred files that is thousands of lines to compare by eye, and a
dropped line or a duplicated comment hides easily among them.

The checks below replace eyeballing with counting. Each one runs against the in-memory
outputs for one input file. If any check fails, nothing is written for that file.

The checks run in both the `plan` phase and the `content` phase. The `renames` phase does
not render outputs, so it does not run them. That means a `--dry-run` already reports
every file that would fail verification, before you touch the tree.

## Check 1: every output parses

Each output file is parsed with Roslyn, using the preview language version. Any error
diagnostic fails the file with this reason. Several errors are joined by a semicolon.

```text
<output path> does not parse: <diagnostic id> <message>
```

When `--require-header` is set, each output must also start with that header, or the file
fails with `<output path> does not start with required file header`.

The input is held to the same rule. An input file with syntax errors is recorded as
`failed` before any split is planned, with the reason `input has syntax errors: ...`.

## Check 2: trivia ownership

Roslyn calls whitespace, comments, and preprocessor directives *trivia*. Trivia is attached
to the tokens around it, which is how a comment stays next to the code it describes. The
Microsoft Learn page on the
[.NET Compiler Platform syntax model](https://learn.microsoft.com/dotnet/csharp/roslyn-sdk/work-with-syntax)
covers the model in detail.

The tool gives each top-level type an owned span:

- If the type has an XML doc comment, the span starts at that doc comment.
- Otherwise the span starts at the declaration. Attributes are part of the declaration
  node, so they are always inside the span.
- The span ends where the declaration ends.

The check takes the text of each input type's owned span and counts how often it appears
across all outputs. The count must be exactly 1. Zero means the type was dropped. Two or
more means it was duplicated. Either fails the file:

```text
<input path>: declaration and owned trivia for <type> appears <count> times across outputs
```

An ordinary `//` comment above a type that has no doc comment is outside the owned span.
The tool moves it with the type, but this check does not prove that. Line conservation
covers part of it, as the next section shows.

Blank lines need the same care. A blank line inside a block comment or an `#if` region is
part of a token's trivia text, not a gap between declarations. The tool removes types and
collapses blank lines through Roslyn trivia, never through text edits, so it cannot rewrite
the inside of a comment. See
[decision-log.md, decision 3](decision-log.md#3-blank-lines-are-handled-through-trivia-never-through-text).

## Check 3: line conservation

This check counts lines. It skips lines that are empty or whitespace only. Every other
line is compared as exact text, including its indentation.

- Every line must appear in the outputs at least as many times as it appears in the input.
  Otherwise the file fails with `<input path>: non-whitespace line was dropped: <line>`.
- A line may appear more often in the outputs than in the input only if it is a prologue
  line. Otherwise the file fails with
  `<input path>: non-prologue line was duplicated: <line>`.

A prologue line is a line outside every type's owned span. In practice that means using
directives and the namespace declaration, which every output needs a copy of.

Here is a three-type input, `Shapes.cs`:

```csharp
using System;

namespace Sample;

/// <summary>A shape with an area.</summary>
public interface IShape
{
    double Area { get; }
}

// Radius is in meters.
[Serializable]
public sealed class Circle : IShape
{
    public double Radius { get; init; }

    public double Area => Math.PI * Radius * Radius;
}

public sealed class Square : IShape
{
    public double Side { get; init; }

    public double Area => Side * Side;
}
```

The split produces `IShape.cs`, `Circle.cs`, and `Square.cs`. Some of the counts:

| Line | Input | Outputs | Result |
| --- | --- | --- | --- |
| `using System;` | 1 | 3 | Allowed. It is a prologue line. |
| `namespace Sample;` | 1 | 3 | Allowed. It is a prologue line. |
| `{` | 3 | 3 | Conserved. |
| `/// <summary>A shape with an area.</summary>` | 1 | 1 | Conserved. Inside the `IShape` owned span. |
| `[Serializable]` | 1 | 1 | Conserved. Inside the `Circle` owned span. |
| `// Radius is in meters.` | 1 | 1 | Conserved. Outside every owned span, so a prologue line. |

The last row is the limit of the check. Because the ordinary comment is a prologue line, the
check would catch it being dropped but would not catch it being copied into two outputs.

The check counts the output text from before any `--require-header` injection. A header
line is never in the source, so counting it would fail every file as a duplicated line.
Whitelisting header lines instead would hide a dropped source line with the same text. See
[decision-log.md, decision 7](decision-log.md#7-outputfile-carries-both-text-and-bodytext).

## Check 4: same type set out as in

The tool parses every output again and collects its top-level types. Top-level means
declared in the compilation unit or directly inside its namespace. The sorted list of type
keys must equal the sorted list from the input, or the file fails with
`<input path>: output top-level type set does not match input`.

A key is the type name, plus its type parameter names for a generic type, for example
`Repository<T>`. The check compares keys only. It does not compare namespaces, modifiers,
base types, or constraints. The repository scan below covers those.

## What happens when a check fails

- The file is left untouched on disk.
- The manifest row is `failed`, and the `reason` column holds the message shown above.
- The process exits with code 1 when any row is `failed`.
- Other files keep going. There is no run level transaction, so files processed before the
  failure stay rewritten. Each file is all or nothing, and the run is not.

All checks finish before the first byte of a file is written. If a write then fails
partway, the tool deletes the files it created for that input and restores the original
from the bytes it read. See
[behavior-contracts.md, WriteOutputs](behavior-contracts.md#writeoutputs) and
[decision-log.md, decision 6](decision-log.md#6-verification-runs-to-completion-before-anything-is-written-per-file).

To list the failures from a `--json` report:

```bash
agentic-roslyn-tool split-types --input src --dry-run --json |
  jq -c '.files[] | select(.status == "failed") | {status, reason}'
```

For an input with a missing closing brace, that prints:

```json
{"status":"failed","reason":"input has syntax errors: CS1513 } expected"}
```

A refusal is not a failure. Files the tool cannot split safely, such as a file with
top-level statements or a `#region` group that crosses type boundaries, are `skipped` with
a reason. See
[decision-log.md, decision 13](decision-log.md#13-refusing-is-the-correct-outcome).

## Before/after repository scan (run it yourself)

The checks above work one file at a time. They cannot see a type that ends up in a
different namespace, or a modifier that changes. A repository-wide scan can. The tool does
not include one. This is a step for the reviewer.

The script below is a .NET 10
[file-based app](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps). It parses
every `.cs` file under a directory, skips `bin` and `obj`, and prints one sorted line per
top-level type:

```text
namespace|kind|name|arity|modifiers|base list|constraints
```

A file that does not parse would give partial rows, so the script writes each parse error
to standard error and exits with code 1.

Save it as `scan.cs` outside the repository you are scanning. A repository that uses
Central Package Management rejects the version in the `#:package` line with error
`NU1008`. The version pin matches the
[Microsoft.CodeAnalysis.CSharp](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp)
version this tool builds against.

```csharp
#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
var rows = new List<string>();

foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
{
    var parts = Path.GetRelativePath(root, path).Split('/', '\\');
    if (parts.Contains("bin") || parts.Contains("obj"))
    {
        continue;
    }

    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path);
    foreach (var error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
    {
        Console.Error.WriteLine(error);
        Environment.ExitCode = 1;
    }

    Walk(tree.GetCompilationUnitRoot().Members, string.Empty);
}

rows.Sort(StringComparer.Ordinal);
rows.ForEach(Console.WriteLine);

void Walk(SyntaxList<MemberDeclarationSyntax> members, string ns)
{
    foreach (var member in members)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax n:
                Walk(n.Members, ns.Length == 0 ? n.Name.ToString() : $"{ns}.{n.Name}");
                break;
            case TypeDeclarationSyntax t:
                rows.Add(Row(ns, t, t.Identifier, t.TypeParameterList, t.Modifiers, t.BaseList, t.ConstraintClauses));
                break;
            case EnumDeclarationSyntax e:
                rows.Add(Row(ns, e, e.Identifier, null, e.Modifiers, e.BaseList, default));
                break;
            case DelegateDeclarationSyntax d:
                rows.Add(Row(ns, d, d.Identifier, d.TypeParameterList, d.Modifiers, null, d.ConstraintClauses));
                break;
        }
    }
}

static string Row(string ns, SyntaxNode node, SyntaxToken name, TypeParameterListSyntax? typeParameters, SyntaxTokenList modifiers, BaseListSyntax? baseList, SyntaxList<TypeParameterConstraintClauseSyntax> constraints)
{
    var arity = typeParameters?.Parameters.Count ?? 0;
    return string.Join("|", ns, node.Kind(), name.Text, arity, Flat(modifiers.ToString()), Flat(baseList?.ToString()), Flat(constraints.ToString()));
}

static string Flat(string? text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
```

For the `Shapes.cs` example above, the three rows for the `Sample` namespace are:

```text
Sample|ClassDeclaration|Circle|0|public sealed|: IShape|
Sample|ClassDeclaration|Square|0|public sealed|: IShape|
Sample|InterfaceDeclaration|IShape|0|public||
```

Run it on the commit before the split and on the commit after, then compare. The `&&`
chain stops at the first scan that fails, so a parse error never reaches the comparison:

```bash
git checkout --detach <commit-before-the-split> &&
  dotnet run ~/tools/scan.cs -- . > ../before.txt &&
  git checkout --detach <commit-after-the-split> &&
  dotnet run ~/tools/scan.cs -- . > ../after.txt &&
  diff ../before.txt ../after.txt && echo "type sets match"
```

An empty diff means every top-level type still exists, once per declaration, with the same
namespace, kind, name, arity, modifiers, base list, and constraints. The scan does not
check member bodies. Line conservation covers those per file. The scan is also syntax only,
so follow it with a full build that treats warnings as errors, then run your tests:

```bash
dotnet build -c Release -p:TreatWarningsAsErrors=true
```

One production run in a closed-source repository followed this process. It processed
7,818 files and split 461 of them. Scans before and after both found 13,561 top-level
types, with zero differences in namespace, name, arity, kind, modifiers, base types, or
constraints. The solution then built with zero warnings under `TreatWarningsAsErrors`. That
repository is closed source, so this result cannot be reproduced from here.

## Review checklist for the pull request

1. Review the plan manifest before anything runs. Every `split` row lists the new files
   and the type each one receives. Every `skipped` and `failed` row carries a reason.
2. Confirm the renames are in their own commit. Then `git log --follow` on a renamed file
   shows its history from before the rename.
3. Confirm the content commit holds only splits, with no unrelated edits.
4. Run `git show --check <content-commit>` to catch whitespace errors.
5. Build with warnings as errors, and run the tests.
6. Run the before/after scan and confirm the diff is empty.

## FAQ

**What is line conservation?**
Every non-whitespace source line appears in the outputs at least as often as in the input.
Only prologue lines, such as usings and the namespace declaration, may appear more often.

**What is trivia in Roslyn?**
Whitespace, comments, and preprocessor directives attached to tokens. It is how comments
stay with the code they describe.

**Does the tool check semantics?**
No. It uses the Roslyn Syntax API and never builds a semantic model. Build the solution
afterward and run your tests.

**If one file fails, are other files rolled back?**
No. Each file is all or nothing, but files processed earlier stay rewritten. Review the
plan first, and commit in phases.

**Can I reproduce the 7,818-file result?**
No. That repository is closed source.

## See also

- [README: Safety and verification](../README.md#safety-and-verification)
- [behavior-contracts.md: VerifyOutputs](behavior-contracts.md#verifyoutputs)
- [behavior-contracts.md: VerifyLineConservation](behavior-contracts.md#verifylineconservation)
- [behavior-contracts.md: Encoding round trip](behavior-contracts.md#encoding-round-trip)
- [decision-log.md](decision-log.md)
