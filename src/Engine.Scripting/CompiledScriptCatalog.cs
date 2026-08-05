using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Engine.Core;

namespace Engine.Scripting;

/// <summary>Identifies one compiled script type by its persistent source asset.</summary>
/// <param name="Asset">Persistent script asset identity.</param>
/// <param name="TypeName">Assembly-qualified metadata type name within the game assembly.</param>
public sealed record CompiledScriptEntry(AssetId Asset, string TypeName);

/// <summary>Loads the generated script catalog and its compiled game assembly.</summary>
public sealed class CompiledScriptCatalog : IScriptTypeCatalog, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly ScriptLoadContext _loadContext;
    private readonly Dictionary<AssetId, Type> _types;
    private bool _disposed;

    /// <summary>Gets the conventional catalog path beside a compiled script assembly.</summary>
    /// <param name="assemblyPath">Compiled game script assembly path.</param>
    /// <returns>Catalog path associated with the assembly.</returns>
    public static string GetCatalogPath(string assemblyPath) =>
        Path.ChangeExtension(Path.GetFullPath(assemblyPath), ".scripts.json");

    /// <summary>Writes a runtime script catalog atomically.</summary>
    /// <param name="path">Destination catalog path.</param>
    /// <param name="entries">Compiler-validated script entries.</param>
    public static void Save(string path, IEnumerable<CompiledScriptEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>Loads and validates a compiled game assembly and generated catalog.</summary>
    /// <param name="assemblyPath">Compiled game script assembly path.</param>
    /// <returns>A catalog owning the collectible game assembly context.</returns>
    public static CompiledScriptCatalog Load(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullAssemblyPath))
            throw new FileNotFoundException("The compiled game script assembly is missing.", fullAssemblyPath);
        var catalogPath = GetCatalogPath(fullAssemblyPath);
        if (!File.Exists(catalogPath))
            throw new FileNotFoundException("The compiled game script catalog is missing. Build scripts in the Editor before running Player.", catalogPath);
        var entries = JsonSerializer.Deserialize<List<CompiledScriptEntry>>(
            File.ReadAllText(catalogPath), JsonOptions)
            ?? throw new InvalidDataException("The compiled game script catalog is empty.");
        var loadContext = new ScriptLoadContext(fullAssemblyPath);
        try
        {
            var assembly = loadContext.LoadMainAssembly();
            return new CompiledScriptCatalog(loadContext, assembly, entries);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    /// <summary>Creates a development catalog by matching one-script-per-file source names.</summary>
    /// <param name="assemblyPath">Compiled game script assembly path.</param>
    /// <param name="scripts">Script asset identities and source-file base names.</param>
    /// <returns>A loaded catalog owning the collectible game assembly context.</returns>
    public static CompiledScriptCatalog RecoverDevelopmentCatalog(
        string assemblyPath,
        IEnumerable<(AssetId Asset, string SourceName)> scripts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentNullException.ThrowIfNull(scripts);
        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullAssemblyPath))
            throw new FileNotFoundException("The compiled game script assembly is missing. Build scripts before running Player.", fullAssemblyPath);
        var loadContext = new ScriptLoadContext(fullAssemblyPath);
        try
        {
            var assembly = loadContext.LoadMainAssembly();
            var attachableTypes = assembly.GetTypes().Where(type =>
                typeof(SceneScript).IsAssignableFrom(type) && !type.IsAbstract &&
                !type.IsGenericTypeDefinition && type.GetConstructor(Type.EmptyTypes) is not null)
                .ToArray();
            var entries = new List<CompiledScriptEntry>();
            foreach (var script in scripts)
            {
                var matches = attachableTypes.Where(type =>
                    string.Equals(type.Name, script.SourceName, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException(
                        $"Script asset '{script.Asset}' expected one compiled type named '{script.SourceName}', but found {matches.Length}. Build scripts in the Editor to generate an authoritative catalog.");
                entries.Add(new CompiledScriptEntry(script.Asset, matches[0].FullName!));
            }
            Save(GetCatalogPath(fullAssemblyPath), entries);
            return new CompiledScriptCatalog(loadContext, assembly, entries);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    /// <inheritdoc/>
    public bool TryResolve(AssetId asset, out Type? scriptType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _types.TryGetValue(asset, out scriptType);
    }

    /// <summary>Unloads the collectible game assembly context.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _types.Clear();
        _loadContext.Unload();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates and validates a catalog against its loaded game assembly.</summary>
    /// <param name="loadContext">Collectible assembly context.</param>
    /// <param name="assembly">Loaded game assembly.</param>
    /// <param name="entries">Compiler-generated entries.</param>
    private CompiledScriptCatalog(
        ScriptLoadContext loadContext,
        Assembly assembly,
        IEnumerable<CompiledScriptEntry> entries)
    {
        _loadContext = loadContext;
        _types = new Dictionary<AssetId, Type>();
        foreach (var entry in entries)
        {
            var type = assembly.GetType(entry.TypeName, false, false)
                ?? throw new InvalidDataException($"Compiled script type '{entry.TypeName}' is missing.");
            if (!typeof(SceneScript).IsAssignableFrom(type) || type.IsAbstract ||
                type.IsGenericTypeDefinition || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException($"Compiled type '{entry.TypeName}' is not an attachable SceneScript.");
            if (!_types.TryAdd(entry.Asset, type))
                throw new InvalidDataException($"Script asset '{entry.Asset}' is duplicated in the catalog.");
        }
    }

    /// <summary>Resolves game dependencies while sharing engine API assemblies.</summary>
    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _assemblyPath;

        /// <summary>Creates a collectible load context.</summary>
        /// <param name="assemblyPath">Main game assembly path.</param>
        public ScriptLoadContext(string assemblyPath) : base(isCollectible: true)
        {
            _assemblyPath = assemblyPath;
            _resolver = new AssemblyDependencyResolver(assemblyPath);
        }

        /// <summary>Loads the main assembly without retaining an output-file lock.</summary>
        /// <returns>The loaded game assembly.</returns>
        public Assembly LoadMainAssembly()
        {
            using var assembly = OpenRead(_assemblyPath);
            var symbolsPath = Path.ChangeExtension(_assemblyPath, ".pdb");
            if (!File.Exists(symbolsPath))
                return LoadFromStream(assembly);
            using var symbols = OpenRead(symbolsPath);
            return LoadFromStream(assembly, symbols);
        }

        /// <inheritdoc/>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Engine.", StringComparison.Ordinal) == true)
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null)
                return null;
            using var stream = OpenRead(path);
            return LoadFromStream(stream);
        }

        /// <summary>Opens a loadable assembly stream without locking build output.</summary>
        /// <param name="path">Assembly path.</param>
        /// <returns>Readable shared stream.</returns>
        private static FileStream OpenRead(string path) => new(path, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }
}
