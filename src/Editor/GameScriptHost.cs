using System.Reflection;
using System.Runtime.Loader;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;

namespace Editor;

/// <summary>
/// Builds, loads, and executes the scripts owned by one game project.
/// </summary>
public sealed class GameScriptHost : IDisposable
{
    private readonly ScriptLoadContext _loadContext;
    private readonly CompiledScriptTypeCatalog? _catalog;
    private SceneScriptRuntime? _runtime;
    private bool _disposed;

    /// <summary>Gets the number of scripts attached to the active play scene.</summary>
    public int ScriptCount => _runtime?.Scripts.Count ?? 0;

    /// <summary>Gets the compiled UUID script catalog when asset discovery was supplied.</summary>
    public IScriptTypeCatalog? Catalog => _catalog;

    /// <summary>Finds the live runtime script created for one play-scene component.</summary>
    /// <param name="component">Play-scene script component.</param>
    /// <param name="script">Live script instance when found.</param>
    /// <returns>True when the active runtime owns the component.</returns>
    public bool TryGetScript(ScriptComponent component, out SceneScript? script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runtime is not null)
            return _runtime.TryGetScript(component, out script);
        script = null;
        return false;
    }

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
    internal static GameScriptHost Load(
        string assemblyPath,
        IReadOnlyList<ScriptAssetDescriptor>? descriptors = null)
    {
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("The script build did not produce its expected assembly.",
                assemblyPath);
        var loadContext = new ScriptLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadMainAssembly();
            var catalog = descriptors is null
                ? null : new CompiledScriptTypeCatalog(assembly, descriptors);
            return new GameScriptHost(loadContext, assembly, catalog);
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
    /// <param name="inputSource">Optional gameplay input source.</param>
    /// <param name="animationService">Optional runtime animation-controller service.</param>
    /// <param name="renderingService">Optional active game-view pipeline service.</param>
    /// <param name="assetService">Optional active project asset service.</param>
    public void LoadScene(Node root, IInputSource? inputSource = null,
        ISceneAnimationService? animationService = null,
        ISceneRenderingService? renderingService = null,
        ISceneAssetService? assetService = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(root);
        _runtime?.Dispose();
        _runtime = null;
        var runtime = new SceneScriptRuntime();
        try
        {
            if (_catalog is null && Enumerate(root).Any(HasScriptComponent))
                throw new InvalidOperationException("The compiled game has no script asset catalog.");
            runtime.Attach(root, (IScriptTypeCatalog?)_catalog ?? EmptyScriptTypeCatalog.Instance,
                inputSource, animationService, renderingService, assetService);
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

    /// <summary>Updates scripts after physics and before rendering.</summary>
    /// <param name="deltaTime">Elapsed time in seconds since the previous update.</param>
    public void LateUpdate(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _runtime?.LateUpdate(deltaTime);
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
    /// <param name="catalog">Optional compiled UUID script catalog.</param>
    private GameScriptHost(
        ScriptLoadContext loadContext,
        Assembly gameAssembly,
        CompiledScriptTypeCatalog? catalog)
    {
        _loadContext = loadContext;
        _catalog = catalog;
    }

    /// <summary>Enumerates a node subtree without exposing scripting internals.</summary>
    /// <param name="root">Subtree root.</param>
    /// <returns>The root and all descendants.</returns>
    private static IEnumerable<Node> Enumerate(Node root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var descendant in Enumerate(child))
            yield return descendant;
    }

    /// <summary>Checks whether one node owns at least one script component.</summary>
    /// <param name="node">Node to inspect.</param>
    /// <returns>True when a script component is attached.</returns>
    private static bool HasScriptComponent(Node node)
    {
        var components = node.Components;
        for (var index = 0; index < components.Count; index++)
        {
            if (components[index] is ScriptComponent)
                return true;
        }
        return false;
    }

    /// <summary>Provides an empty catalog for scenes without attached scripts.</summary>
    private sealed class EmptyScriptTypeCatalog : IScriptTypeCatalog
    {
        /// <summary>Gets the shared empty catalog.</summary>
        internal static EmptyScriptTypeCatalog Instance { get; } = new();

        /// <inheritdoc/>
        public bool TryResolve(AssetId asset, out Type? scriptType)
        {
            scriptType = null;
            return false;
        }
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
