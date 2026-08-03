using System.Reflection;
using System.Runtime.Loader;
using Engine.Core;
using Engine.Scripting;

namespace Editor;

/// <summary>
/// Builds, loads, and executes the scripts owned by one game project.
/// </summary>
public sealed class GameScriptHost : IDisposable
{
    private readonly ScriptLoadContext _loadContext;
    private readonly Assembly _gameAssembly;
    private SceneScriptRuntime? _runtime;
    private bool _disposed;

    /// <summary>Gets the number of scripts attached to the active play scene.</summary>
    public int ScriptCount => _runtime?.Scripts.Count ?? 0;

    /// <summary>
    /// Builds and loads a generated game script project.
    /// </summary>
    /// <param name="workspace">Game scripting workspace paths.</param>
    /// <returns>A host containing the compiled script assembly.</returns>
    public static GameScriptHost BuildAndLoad(ScriptingWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        using var compiler = new GameScriptCompiler(workspace);
        return compiler.BuildAndLoad();
    }

    /// <summary>Loads one compiled script assembly without retaining file handles on build outputs.</summary>
    /// <param name="assemblyPath">Absolute compiled assembly path.</param>
    /// <returns>A host containing the compiled script assembly.</returns>
    internal static GameScriptHost Load(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("The script build did not produce its expected assembly.",
                assemblyPath);
        var loadContext = new ScriptLoadContext(assemblyPath);
        try
        {
            return new GameScriptHost(loadContext, loadContext.LoadMainAssembly());
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    /// <summary>
    /// Attaches compiled scripts to a newly active scene and starts their lifecycle.
    /// </summary>
    /// <param name="root">Synthetic active scene root.</param>
    public void LoadScene(Node root)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(root);
        _runtime?.Dispose();
        _runtime = null;
        var runtime = new SceneScriptRuntime();
        try
        {
            runtime.Attach(root, ResolveType);
            runtime.Start();
            _runtime = runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    /// <summary>Updates scripts attached to the active scene.</summary>
    /// <param name="deltaTime">Elapsed time in seconds since the previous update.</param>
    public void Update(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _runtime?.Update(deltaTime);
    }

    /// <summary>Stops and detaches scripts belonging to the active play scene.</summary>
    public void UnloadScene()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _runtime?.Dispose();
        _runtime = null;
    }

    /// <summary>Stops scripts and unloads the collectible game assembly context.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            if (_runtime is not null)
                UnloadScene();
        }
        finally
        {
            _loadContext.Unload();
            _disposed = true;
        }
    }

    /// <summary>
    /// Creates a host around an already loaded game script assembly.
    /// </summary>
    /// <param name="loadContext">Collectible assembly load context.</param>
    /// <param name="gameAssembly">Compiled game assembly.</param>
    private GameScriptHost(ScriptLoadContext loadContext, Assembly gameAssembly)
    {
        _loadContext = loadContext;
        _gameAssembly = gameAssembly;
    }

    /// <summary>
    /// Resolves one serialized script type from the compiled game assembly.
    /// </summary>
    /// <param name="typeName">Full or assembly-qualified script type name.</param>
    /// <returns>The resolved type, or null when it is not in the game assembly.</returns>
    private Type? ResolveType(string typeName)
    {
        var separator = typeName.IndexOf(',');
        var fullName = separator < 0 ? typeName : typeName[..separator];
        return _gameAssembly.GetType(fullName.Trim(), throwOnError: false, ignoreCase: false);
    }

    /// <summary>
    /// Resolves private game dependencies while sharing engine API assemblies with the host.
    /// </summary>
    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        /// <summary>
        /// Creates a collectible load context for one compiled game assembly.
        /// </summary>
        /// <param name="assemblyPath">Compiled game assembly path.</param>
        public ScriptLoadContext(string assemblyPath)
            : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
            AssemblyPath = Path.GetFullPath(assemblyPath);
        }

        private string AssemblyPath { get; }

        /// <summary>Loads the main assembly and optional symbols from non-locking streams.</summary>
        /// <returns>The loaded game assembly.</returns>
        internal Assembly LoadMainAssembly()
        {
            using var assembly = OpenRead(AssemblyPath);
            var symbolsPath = Path.ChangeExtension(AssemblyPath, ".pdb");
            if (!File.Exists(symbolsPath))
                return LoadFromStream(assembly);
            using var symbols = OpenRead(symbolsPath);
            return LoadFromStream(assembly, symbols);
        }

        /// <summary>
        /// Resolves a managed dependency from the game output directory.
        /// </summary>
        /// <param name="assemblyName">Requested assembly name.</param>
        /// <returns>The loaded dependency, or null to share the default-context assembly.</returns>
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

        /// <summary>Opens an assembly without preventing replacement or deletion.</summary>
        /// <param name="path">Assembly path.</param>
        /// <returns>Readable assembly stream.</returns>
        private static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
    }
}
