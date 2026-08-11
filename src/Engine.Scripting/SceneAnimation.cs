using Engine.Core;
using Engine.Graphics;

namespace Engine.Scripting;

/// <summary>Resolves runtime animation controllers owned by an active scene.</summary>
public interface ISceneAnimationService
{
    /// <summary>Gets the controller bound to a node or its animated mesh child.</summary>
    /// <param name="node">Scene node used as the lookup origin.</param>
    /// <returns>The controller, or null when no animated mesh is bound.</returns>
    AnimationController? Get(Node node);

    /// <summary>Gets the required controller bound to a node or its animated mesh child.</summary>
    /// <param name="node">Scene node used as the lookup origin.</param>
    /// <returns>The bound controller.</returns>
    AnimationController GetRequired(Node node);
}

/// <summary>Provides the empty animation service used by headless or unbound script contexts.</summary>
internal sealed class EmptySceneAnimationService : ISceneAnimationService
{
    /// <summary>Gets the shared empty service.</summary>
    internal static EmptySceneAnimationService Instance { get; } = new();

    /// <inheritdoc/>
    public AnimationController? Get(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return null;
    }

    /// <inheritdoc/>
    public AnimationController GetRequired(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        throw new InvalidOperationException(
            $"Node '{node.Name}' has no runtime animation controller.");
    }
}

/// <summary>Maps active scene nodes to runtime animation controllers.</summary>
public sealed class SceneAnimationRegistry : ISceneAnimationService, IDisposable
{
    private readonly Dictionary<Node, AnimationController> _controllers =
        new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    /// <inheritdoc/>
    public AnimationController? Get(Node node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(node);
        if (_controllers.TryGetValue(node, out var direct))
            return direct;
        return FindDescendant(node);
    }

    /// <inheritdoc/>
    public AnimationController GetRequired(Node node)
    {
        return Get(node) ?? throw new InvalidOperationException(
            $"Node '{node.Name}' has no runtime animation controller.");
    }

    /// <summary>Registers one controller under its animated mesh node.</summary>
    /// <param name="node">Animated mesh instance.</param>
    /// <param name="controller">Runtime controller.</param>
    public void Register(Node node, AnimationController controller)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(controller);
        if (!_controllers.TryAdd(node, controller))
            throw new InvalidOperationException(
                $"Node '{node.Name}' already has an animation controller.");
    }

    /// <summary>Removes a controller mapping without disposing the controller.</summary>
    /// <param name="node">Animated mesh instance.</param>
    /// <returns>True when a mapping was removed.</returns>
    public bool Unregister(Node node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(node);
        return _controllers.Remove(node);
    }

    /// <summary>Invalidates and disposes all registered runtime controllers.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var controller in _controllers.Values)
            controller.Dispose();
        _controllers.Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>Finds the first registered animated mesh in a node subtree.</summary>
    /// <param name="node">Subtree root.</param>
    /// <returns>The first controller in depth-first authored order.</returns>
    private AnimationController? FindDescendant(Node node)
    {
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            if (_controllers.TryGetValue(child, out var controller))
                return controller;
            if (FindDescendant(child) is { } descendant)
                return descendant;
        }
        return null;
    }
}
