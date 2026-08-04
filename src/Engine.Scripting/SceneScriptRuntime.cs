using Engine.Core;

namespace Engine.Scripting;

/// <summary>
/// Owns script instances attached to one active scene.
/// </summary>
public sealed class SceneScriptRuntime : IDisposable
{
    private readonly List<SceneScript> _scripts = new();
    private bool _started;
    private bool _disposed;

    /// <summary>Gets the script instances attached to the scene.</summary>
    public IReadOnlyList<SceneScript> Scripts => _scripts;

    /// <summary>
    /// Attaches scripts declared by nodes in a scene graph.
    /// </summary>
    /// <param name="root">Synthetic scene root.</param>
    /// <param name="catalog">Catalog resolving persistent script assets to compiled types.</param>
    public void Attach(Node root, IScriptTypeCatalog catalog)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(catalog);
        if (_scripts.Count != 0 || _started)
            throw new InvalidOperationException("The runtime already has an attached scene.");

        var context = new SceneContext(root);
        foreach (var node in SceneContext.Enumerate(root))
        {
            if (node.ScriptId is not { } scriptId)
                continue;
            if (!catalog.TryResolve(scriptId, out var type) || type is null)
                throw new InvalidOperationException($"Script asset '{scriptId}' was not found.");
            if (!typeof(SceneScript).IsAssignableFrom(type) || type.IsAbstract)
                throw new InvalidOperationException(
                    $"Script asset '{scriptId}' must resolve to a concrete {nameof(SceneScript)}.");
            if (Activator.CreateInstance(type) is not SceneScript script)
                throw new InvalidOperationException(
                    $"Script asset '{scriptId}' must have a public parameterless constructor.");
            script.Owner = node;
            script.Scene = context;
            _scripts.Add(script);
        }
    }

    /// <summary>Starts all attached scripts after attachment has completed.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;
        _started = true;
        foreach (var script in _scripts)
            script.OnReady();
    }

    /// <summary>Updates all active scripts.</summary>
    /// <param name="deltaTime">Elapsed time in seconds since the previous update.</param>
    public void Update(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
            throw new InvalidOperationException("The script runtime has not been started.");
        foreach (var script in _scripts)
            script.OnUpdate(deltaTime);
    }

    /// <summary>Detaches scripts and releases lifecycle state.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        for (var index = _scripts.Count - 1; index >= 0; index--)
        {
            try
            {
                _scripts[index].OnDestroy();
            }
            catch (Exception exception)
            {
                failures ??= new List<Exception>();
                failures.Add(exception);
            }
        }
        _scripts.Clear();
        _disposed = true;
        if (failures is not null)
            throw new AggregateException("One or more scripts failed during destruction.", failures);
    }
}
