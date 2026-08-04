using Engine.Assets;
using Engine.Core;
using Engine.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Editor;

/// <summary>Describes one compiler-discovered attachable script asset.</summary>
/// <param name="Asset">Persistent C# source asset identity.</param>
/// <param name="SourcePath">Normalized project-relative source path.</param>
/// <param name="TypeName">Fully qualified compiled type name.</param>
public sealed record ScriptAssetDescriptor(
    AssetId Asset,
    string SourcePath,
    string TypeName);

/// <summary>Reports one semantic script discovery problem.</summary>
/// <param name="Asset">Source asset identity.</param>
/// <param name="SourcePath">Normalized project-relative source path.</param>
/// <param name="Message">Actionable semantic diagnostic.</param>
public sealed record ScriptAssetDiagnostic(
    AssetId Asset,
    string SourcePath,
    string Message);

/// <summary>Contains valid script descriptors and semantic discovery diagnostics.</summary>
/// <param name="Scripts">Valid one-script-per-source descriptors.</param>
/// <param name="Diagnostics">Invalid or ambiguous script diagnostics.</param>
public sealed record ScriptAnalysisResult(
    IReadOnlyList<ScriptAssetDescriptor> Scripts,
    IReadOnlyList<ScriptAssetDiagnostic> Diagnostics);

/// <summary>Uses Roslyn symbols to discover attachable SceneScript classes by source asset.</summary>
public static class CSharpScriptAnalyzer
{
    /// <summary>Analyzes indexed C# assets using the dependencies of a compiled game output.</summary>
    /// <param name="database">Project asset database.</param>
    /// <param name="assemblyDirectory">Compiled game output directory containing resolved dependencies.</param>
    /// <returns>Valid script descriptors and semantic diagnostics.</returns>
    public static ScriptAnalysisResult Analyze(
        AssetDatabase database,
        string assemblyDirectory)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyDirectory);
        var records = database.Assets.Where(record => record.Importer == "csharp-script")
            .OrderBy(record => record.ProjectPath, StringComparer.Ordinal).ToArray();
        var treesByRecord = records.Select(record =>
        {
            var path = Path.Combine(database.ProjectRoot,
                record.ProjectPath.Replace('/', Path.DirectorySeparatorChar));
            return (Record: record, Tree: CSharpSyntaxTree.ParseText(
                File.ReadAllText(path), path: path));
        }).ToArray();
        var references = CreateReferences(assemblyDirectory);
        var compilation = CSharpCompilation.Create("Nico.ScriptAnalysis",
            treesByRecord.Select(item => item.Tree), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var sceneScript = compilation.GetTypeByMetadataName(typeof(SceneScript).FullName!)
            ?? throw new InvalidOperationException("SceneScript could not be resolved for analysis.");
        var scripts = new List<ScriptAssetDescriptor>();
        var diagnostics = new List<ScriptAssetDiagnostic>();

        foreach (var item in treesByRecord)
        {
            var model = compilation.GetSemanticModel(item.Tree);
            var candidates = item.Tree.GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Select(declaration => model.GetDeclaredSymbol(declaration))
                .OfType<INamedTypeSymbol>()
                .Where(symbol => DerivesFrom(symbol, sceneScript))
                .Cast<ISymbol>()
                .Distinct(SymbolEqualityComparer.Default)
                .OfType<INamedTypeSymbol>()
                .ToArray();
            var valid = candidates.Where(IsAttachable).ToArray();
            if (valid.Length == 1 && candidates.Length == 1)
            {
                scripts.Add(new ScriptAssetDescriptor(item.Record.Id, item.Record.ProjectPath,
                    GetRuntimeTypeName(valid[0])));
                continue;
            }
            if (candidates.Length == 0)
                continue;
            var message = candidates.Length > 1
                ? "A script source asset must declare exactly one SceneScript class."
                : "SceneScript classes must be public, concrete, non-generic, and have a public parameterless constructor.";
            diagnostics.Add(new ScriptAssetDiagnostic(
                item.Record.Id, item.Record.ProjectPath, message));
        }
        return new ScriptAnalysisResult(scripts, diagnostics);
    }

    /// <summary>Creates compilation references from runtime and compiled game dependencies.</summary>
    /// <param name="assemblyDirectory">Compiled game output directory.</param>
    /// <returns>Unique readable metadata references.</returns>
    private static IReadOnlyList<MetadataReference> CreateReferences(string assemblyDirectory)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trusted))
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
                paths.Add(path);
        }
        paths.Add(typeof(Node).Assembly.Location);
        paths.Add(typeof(SceneScript).Assembly.Location);
        if (Directory.Exists(assemblyDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(assemblyDirectory, "*.dll"))
                paths.Add(path);
        }
        var references = new List<MetadataReference>();
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (BadImageFormatException)
            {
            }
        }
        return references;
    }

    /// <summary>Returns whether a type derives from the SceneScript contract.</summary>
    /// <param name="symbol">Candidate declared class.</param>
    /// <param name="sceneScript">Resolved SceneScript base symbol.</param>
    /// <returns>True when the candidate has SceneScript in its base chain.</returns>
    private static bool DerivesFrom(INamedTypeSymbol symbol, INamedTypeSymbol sceneScript)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, sceneScript))
                return true;
        }
        return false;
    }

    /// <summary>Returns whether a discovered script can be instantiated by the runtime.</summary>
    /// <param name="symbol">Discovered SceneScript symbol.</param>
    /// <returns>True for public concrete non-generic classes with a public parameterless constructor.</returns>
    private static bool IsAttachable(INamedTypeSymbol symbol)
    {
        return symbol.DeclaredAccessibility == Accessibility.Public &&
               !symbol.IsAbstract && !symbol.IsGenericType &&
               symbol.InstanceConstructors.Any(constructor =>
                   constructor.DeclaredAccessibility == Accessibility.Public &&
                   constructor.Parameters.Length == 0);
    }

    /// <summary>Formats the metadata name accepted by Assembly.GetType, including nested classes.</summary>
    /// <param name="symbol">Compiled script symbol.</param>
    /// <returns>Fully qualified runtime type name.</returns>
    private static string GetRuntimeTypeName(INamedTypeSymbol symbol)
    {
        var typeNames = new Stack<string>();
        for (var current = symbol; current is not null; current = current.ContainingType)
            typeNames.Push(current.MetadataName);
        var typeName = string.Join('+', typeNames);
        return symbol.ContainingNamespace.IsGlobalNamespace
            ? typeName : $"{symbol.ContainingNamespace.ToDisplayString()}.{typeName}";
    }
}
