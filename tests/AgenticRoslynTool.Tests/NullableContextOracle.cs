using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Reads the nullable context at each top level type through the compiler's own semantic
/// model. The tool never builds one, so this is an oracle outside the code under test.
/// </summary>
/// <remarks>
/// It checks every build the source can describe: each project default, and each set of
/// the symbols its <c>#if</c> conditions name. The tool folds directives with no symbols
/// defined, so an oracle that did the same would share its blind spot. Review found a wrong
/// write that only a defined symbol shows, after five rounds that checked without one.
/// </remarks>
internal static class NullableContextOracle
{
    private static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    /// <summary>
    /// Maps each top level type key to the nullable context at each of its
    /// <see cref="NullableCheckPoints"/>, in order.
    /// </summary>
    internal static Dictionary<string, NullableContext[]> ContextByType(string text, NullableContextOptions projectDefault, IEnumerable<string>? symbols = null)
    {
        var tree = CSharpSyntaxTree.ParseText(text, Parse.WithPreprocessorSymbols(symbols ?? Array.Empty<string>()));
        var model = Compile(tree, projectDefault).GetSemanticModel(tree);
        return TopLevelType.Find(tree.GetCompilationUnitRoot()).ToDictionary(
            type => type.Key,
            type => NullableCheckPoints.Of(type).Select(model.GetNullableContext).ToArray());
    }

    /// <summary>Returns the error and warning ids the compiler reports for one file, under the project default given.</summary>
    internal static string[] DiagnosticIds(string text, NullableContextOptions projectDefault = NullableContextOptions.Disable)
    {
        var tree = CSharpSyntaxTree.ParseText(text, Parse);
        return Compile(tree, projectDefault).GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => d.Id)
            .ToArray();
    }

    /// <summary>
    /// Asserts that every type keeps, in its output file, the nullable context it had in
    /// the source file. Reads every <c>.cs</c> file under <paramref name="root"/>.
    /// </summary>
    internal static void AssertPreserved(string source, string root)
    {
        var outputs = Directory.GetFiles(root, "*.cs").Select(File.ReadAllText).ToArray();
        Assert.Null(FindChange(source, outputs));
    }

    /// <summary>
    /// The first type whose context differs between the source and the outputs, worded with
    /// the build that shows it, or null when every build agrees. Builds are each project
    /// default, disable and enable, crossed with each set of the source's <c>#if</c> symbols.
    /// Checking one default lets <c>#nullable disable</c> pass where the source said
    /// <c>#nullable restore</c>.
    /// </summary>
    internal static string? FindChange(string source, IReadOnlyList<string> outputs)
    {
        foreach (var symbols in PreprocessorSymbolSets.Of(source, Parse))
        {
            foreach (var projectDefault in new[] { NullableContextOptions.Disable, NullableContextOptions.Enable })
            {
                var build = $"default {projectDefault}, symbols [{string.Join(",", symbols)}]";
                var expected = ContextByType(source, projectDefault, symbols);
                var actual = new Dictionary<string, NullableContext[]>();
                foreach (var output in outputs)
                {
                    foreach (var (key, contexts) in ContextByType(output, projectDefault, symbols))
                    {
                        if (!actual.TryAdd(key, contexts))
                        {
                            return $"{build}: {key} is declared in more than one output";
                        }
                    }
                }

                if (!expected.Keys.Order().SequenceEqual(actual.Keys.Order()))
                {
                    return $"{build}: source declares [{string.Join(",", expected.Keys.Order())}], outputs declare [{string.Join(",", actual.Keys.Order())}]";
                }

                foreach (var (key, contexts) in expected)
                {
                    if (!contexts.SequenceEqual(actual[key]))
                    {
                        return $"{key} under {build}: source contexts [{string.Join(", ", contexts)}], output contexts [{string.Join(", ", actual[key])}]";
                    }
                }
            }
        }

        return null;
    }

    private static CSharpCompilation Compile(SyntaxTree tree, NullableContextOptions projectDefault)
    {
        return CSharpCompilation.Create(
            "Oracle",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: projectDefault));
    }
}
