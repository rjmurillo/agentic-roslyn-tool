using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticRoslynTool.Tests;

// Compiles source for the file-local type tests. The tool's own verifier only parses, and
// a parse cannot show that a moved type still binds or that a file-local type kept its
// name. The compiler can. Every tree carries its real path, because the compiler builds a
// file-local type's metadata name from that path.
internal static class FileLocalCompiler
{
    // Documentation mode is on so an unresolved cref is reported, as it is in a build
    // that writes a documentation file.
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview)
        .WithDocumentationMode(DocumentationMode.Diagnose);

    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray());

    // Compiles every C# file a directory holds.
    public static CSharpCompilation CompileDirectory(string root, params string[] symbols)
    {
        var files = Directory.GetFiles(root, "*.cs").Order(StringComparer.Ordinal).Select(path => (path, File.ReadAllText(path))).ToArray();
        return Compile(files, symbols);
    }

    public static CSharpCompilation Compile((string Path, string Text)[] files, params string[] symbols)
    {
        var options = ParseOptions.WithPreprocessorSymbols(symbols);
        return CSharpCompilation.Create(
            "FileLocalProbe",
            files.Select(file => CSharpSyntaxTree.ParseText(file.Text, options, path: file.Path)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    // Every warning and error as "ID message", sorted. Empty means a clean build.
    public static string[] Diagnostics(CSharpCompilation compilation)
    {
        return compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
            .Select(diagnostic => diagnostic.Id + " " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    // The metadata name the compiler gives a file-local type. It carries the file's name.
    public static string MetadataName(CSharpCompilation compilation, string typeName)
    {
        return compilation.GetSymbolsWithName(typeName, SymbolFilter.Type).OfType<INamedTypeSymbol>().Single(symbol => symbol.IsFileLocal).MetadataName;
    }

    // How many types of one name the compilation declares, across every file.
    public static int CountDeclared(CSharpCompilation compilation, string typeName)
    {
        return compilation.GetSymbolsWithName(typeName, SymbolFilter.Type).Count();
    }

    // The type one identifier binds to, with its namespace, and whether it is file-local.
    // The identifier is looked up inside the declaration of the type named inType.
    public static string BoundType(CSharpCompilation compilation, string inType, string identifier)
    {
        var use = compilation.SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            .Where(declaration => declaration.Identifier.ValueText == inType)
            .SelectMany(declaration => declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
            .First(name => name.Identifier.ValueText == identifier);
        var symbol = (INamedTypeSymbol)compilation.GetSemanticModel(use.SyntaxTree).GetSymbolInfo(use).Symbol!;
        return (symbol.IsFileLocal ? "file " : string.Empty) + symbol.ToDisplayString();
    }

    // What a split would write if nothing refused it. The type named for the file stays
    // with the file-local types, and every other ordinary type moves to its own file. A
    // test compiles this to show what a refusal prevents. Nothing is written to disk.
    public static (string Path, string Text)[] SplitUnchecked(string path, string text)
    {
        return BuildUnchecked(path, text).Outputs.Select(output => (output.Path, output.Text)).ToArray();
    }

    // The same unchecked split, as the types and rendered outputs the verifier takes.
    public static (TopLevelType[] Types, OutputFile[] Outputs) BuildUnchecked(string path, string text)
    {
        var root = CSharpSyntaxTree.ParseText(text, ParseOptions, path: path).GetCompilationUnitRoot();
        var types = TopLevelType.Find(root).ToArray();
        var keep = types.First(type => type.Name == Path.GetFileNameWithoutExtension(path));
        var files = types
            .Where(type => !type.HasFileModifier)
            .Select(type => ReferenceEquals(type, keep)
                ? new PlannedFile(path, type, IsKept: true)
                : new PlannedFile(Path.Combine(Path.GetDirectoryName(path)!, type.Name + ".cs"), type, IsKept: false))
            .ToArray();
        var plan = new SplitPlan(keep, path, GitMove: false, Note: null, SkipReason: null, files);
        var source = EncodedSource.FromBytes(System.Text.Encoding.UTF8.GetBytes(text));
        return (types, OutputBuilder.BuildOutputs(source, root, types, plan, string.Empty));
    }
}
