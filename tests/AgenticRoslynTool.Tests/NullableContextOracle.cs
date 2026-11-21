using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AgenticRoslynTool.Tests;

/// <summary>
/// Reads the nullable context at each top level type through the compiler's own semantic
/// model. The tool never builds one, so this is an oracle outside the code under test.
/// </summary>
internal static class NullableContextOracle
{
    private static readonly CSharpParseOptions Parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    /// <summary>Maps each top level type key to the nullable context at its first token.</summary>
    internal static Dictionary<string, NullableContext> ContextByType(string text, NullableContextOptions projectDefault)
    {
        var tree = CSharpSyntaxTree.ParseText(text, Parse);
        var model = Compile(tree, projectDefault).GetSemanticModel(tree);
        return TopLevelType.Find(tree.GetCompilationUnitRoot())
            .ToDictionary(type => type.Key, type => model.GetNullableContext(type.Node.SpanStart));
    }

    /// <summary>Returns the error and warning ids the compiler reports for one file.</summary>
    internal static string[] DiagnosticIds(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, Parse);
        return Compile(tree, NullableContextOptions.Disable).GetDiagnostics()
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => d.Id)
            .ToArray();
    }

    /// <summary>
    /// Asserts that every type keeps, in its output file, the nullable context it had in
    /// the source file. Reads every <c>.cs</c> file under <paramref name="root"/>. Checks
    /// under a project default of both disable and enable, so a <c>#nullable disable</c>
    /// written where the source had <c>#nullable restore</c> fails.
    /// </summary>
    internal static void AssertPreserved(string source, string root)
    {
        foreach (var projectDefault in new[] { NullableContextOptions.Disable, NullableContextOptions.Enable })
        {
            var expected = ContextByType(source, projectDefault);
            var actual = new Dictionary<string, NullableContext>();
            foreach (var path in Directory.GetFiles(root, "*.cs"))
            {
                foreach (var (key, context) in ContextByType(File.ReadAllText(path), projectDefault))
                {
                    actual.Add(key, context);
                }
            }

            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            foreach (var (key, context) in expected)
            {
                Assert.True(context == actual[key], $"{key} under {projectDefault}: source context {context}, output context {actual[key]}");
            }
        }
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
