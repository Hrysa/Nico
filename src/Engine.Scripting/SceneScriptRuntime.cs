using Engine.Core;
using Engine.Graphics;

namespace Engine.Scripting;

/// <summary>
/// Owns script instances attached to one active scene.
/// </summary>
public sealed class SceneScriptRuntime : IDisposable
{
    private readonly List<SceneScript> _scripts = new();
    private SceneContext? _context;
    private bool _started;
    private bool _disposed;

    /// <summary>Gets the script instances attached to the scene.</summary>
    public IReadOnlyList<SceneScript> Scripts => _scripts;

    /// <summary>Finds the runtime instance created for one authored component.</summary>
    /// <param name="component">Script component identity.</param>
    /// <param name="script">Attached runtime instance when found.</param>
    /// <returns>True when the component belongs to this active runtime.</returns>
    public bool TryGetScript(ScriptComponent component, out SceneScript? script)
    {
        ArgumentNullException.ThrowIfNull(component);
        for (var index = 0; index < _scripts.Count; index++)
        {
            if (!ReferenceEquals(_scripts[index].Component, component))
                continue;
            script = _scripts[index];
            return true;
        }
        script = null;
        return false;
    }

    /// <summary>
    /// Attaches scripts declared by nodes in a scene graph.
    /// </summary>
    /// <param name="root">Synthetic scene root.</param>
    /// <param name="catalog">Catalog resolving persistent script assets to compiled types.</param>
    /// <param name="inputSource">Optional renderer-independent gameplay input source.</param>
    /// <param name="animationService">Optional runtime animation-controller service.</param>
    /// <param name="renderingService">Optional active game-view pipeline service.</param>
    public void Attach(Node root, IScriptTypeCatalog catalog, IInputSource? inputSource = null,
        ISceneAnimationService? animationService = null,
        ISceneRenderingService? renderingService = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(catalog);
        if (_scripts.Count != 0 || _started)
            throw new InvalidOperationException("The runtime already has an attached scene.");

        var context = new SceneContext(root, inputSource, animationService, renderingService);
        _context = context;
        foreach (var node in SceneContext.Enumerate(root))
        {
            var components = node.Components;
            for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                if (components[componentIndex] is ScriptComponent component)
                    AttachScript(node, component, context, catalog);
            }
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
        try
        {
            foreach (var script in _scripts)
            {
                if (script.Component.Enabled)
                    script.OnUpdate(deltaTime);
            }
        }
        finally
        {
            _context?.Input.EndUpdate();
        }
    }

    /// <summary>Updates enabled scripts after physics and before rendering.</summary>
    /// <param name="deltaTime">Elapsed time in seconds since the previous update.</param>
    public void LateUpdate(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_started)
            throw new InvalidOperationException("The script runtime has not been started.");
        foreach (var script in _scripts)
        {
            if (script.Component.Enabled)
                script.OnLateUpdate(deltaTime);
        }
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
        _context?.Input.Dispose();
        _context = null;
        _disposed = true;
        if (failures is not null)
            throw new AggregateException("One or more scripts failed during destruction.", failures);
    }

    /// <summary>Creates one runtime script and applies authored generated-property values.</summary>
    /// <param name="owner">Node owning the component.</param>
    /// <param name="component">Authored script component.</param>
    /// <param name="context">Active scene services.</param>
    /// <param name="catalog">Compiled script type catalog.</param>
    private void AttachScript(
        Node owner,
        ScriptComponent component,
        SceneContext context,
        IScriptTypeCatalog catalog)
    {
        var scriptId = component.ScriptId;
        if (!catalog.TryResolve(scriptId, out var type) || type is null)
            throw new InvalidOperationException($"Script asset '{scriptId}' was not found.");
        if (!typeof(SceneScript).IsAssignableFrom(type) || type.IsAbstract)
            throw new InvalidOperationException(
                $"Script asset '{scriptId}' must resolve to a concrete {nameof(SceneScript)}.");
        if (Activator.CreateInstance(type) is not SceneScript script)
            throw new InvalidOperationException(
                $"Script asset '{scriptId}' must have a public parameterless constructor.");
        script.Owner = owner;
        script.Component = component;
        script.Scene = context;
        var overrides = component.PropertyOverrides;
        for (var index = 0; index < overrides.Count; index++)
        {
            var propertyOverride = overrides[index];
            if (ObservedValue.TryFromSerialized(propertyOverride.Value, out var value))
                script.TrySetObservedValue(propertyOverride.PropertyId, value);
        }
        _scripts.Add(script);
    }
}
