using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Builds diagnostic geometry for one supported node or component type.</summary>
public interface IScenePreviewProvider
{
    /// <summary>Gets the category controlled by editor visibility settings.</summary>
    ScenePreviewCategory Category { get; }

    /// <summary>Gets whether this provider accepts one authored object.</summary>
    /// <param name="value">Node or component candidate.</param>
    /// <returns>True when this provider can build its preview.</returns>
    bool Supports(object value);

    /// <summary>Appends preview primitives for one authored object.</summary>
    /// <param name="node">Owning transformed node.</param>
    /// <param name="value">Supported node or component.</param>
    /// <param name="pickingId">Stable picking identity.</param>
    /// <param name="selected">Whether the owning node is selected.</param>
    /// <param name="hovered">Whether this exact preview is hovered.</param>
    /// <param name="destination">Reusable primitive destination.</param>
    void Build(
        Node3D node,
        object value,
        ScenePreviewPickingId pickingId,
        bool selected,
        bool hovered,
        ScenePreviewList destination);
}

/// <summary>Invalidates provider-owned decoded asset previews after source edits.</summary>
public interface IScenePreviewAssetCache
{
    /// <summary>Removes one persistent resource from the provider cache.</summary>
    /// <param name="reference">Edited or reimported resource.</param>
    void Invalidate(AssetReference reference);
}

/// <summary>Runs registered preview providers over a scene hierarchy without mutating it.</summary>
public sealed class ScenePreviewRegistry
{
    private readonly List<IScenePreviewProvider> _providers = [];
    private readonly bool[] _categoryVisibility = [true, true, true, true];
    private readonly Dictionary<object, ulong> _pickingIds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _hiddenValues = new(ReferenceEqualityComparer.Instance);
    private ulong _nextPickingId = 1;

    /// <summary>Registers a provider in deterministic evaluation order.</summary>
    /// <param name="provider">Provider to append.</param>
    public void Register(IScenePreviewProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers.Add(provider);
    }

    /// <summary>Changes visibility for one diagnostic category.</summary>
    /// <param name="category">Category to change.</param>
    /// <param name="visible">Whether providers in the category run.</param>
    public void SetCategoryVisible(ScenePreviewCategory category, bool visible)
    {
        _categoryVisibility[(int)category] = visible;
    }

    /// <summary>Changes visibility for one node or component preview only.</summary>
    /// <param name="value">Authored node or component.</param><param name="visible">Desired visibility.</param>
    public void SetPreviewVisible(object value, bool visible)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (visible)
            _hiddenValues.Remove(value);
        else
            _hiddenValues.Add(value);
    }

    /// <summary>Invalidates decoded provider data for one edited asset output.</summary>
    /// <param name="reference">Edited or reimported resource.</param>
    public void InvalidateAsset(AssetReference reference)
    {
        for (var index = 0; index < _providers.Count; index++)
        {
            if (_providers[index] is IScenePreviewAssetCache cache)
                cache.Invalidate(reference);
        }
    }

    /// <summary>Builds all enabled previews beneath a scene root.</summary>
    /// <param name="root">Scene hierarchy root.</param>
    /// <param name="selectedNode">Currently selected node.</param>
    /// <param name="destination">Reusable output list.</param>
    /// <param name="hovered">Preview hovered during the preceding pointer update.</param>
    public void Build(Node root, Node3D? selectedNode, ScenePreviewList destination,
        ScenePreviewPickingId? hovered = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        BuildNode(root, selectedNode, hovered, destination);
    }

    /// <summary>Recursively evaluates providers in hierarchy and component order.</summary>
    /// <param name="node">Current hierarchy node.</param>
    /// <param name="selectedNode">Currently selected node.</param>
    /// <param name="hovered">Currently hovered preview identity.</param>
    /// <param name="destination">Reusable output list.</param>
    private void BuildNode(
        Node node,
        Node3D? selectedNode,
        ScenePreviewPickingId? hovered,
        ScenePreviewList destination)
    {
        if (node is Node3D node3D)
        {
            BuildValue(node3D, node3D,
                new ScenePreviewPickingId(GetPickingId(node3D), node3D),
                ReferenceEquals(node3D, selectedNode),
                hovered is { Component: null } && ReferenceEquals(hovered.Value.Node, node3D),
                destination);
            var components = node.Components;
            for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                var component = components[componentIndex];
                if (!component.Enabled)
                    continue;
                BuildValue(node3D, component,
                    new ScenePreviewPickingId(GetPickingId(component), node3D, component),
                    ReferenceEquals(node3D, selectedNode),
                    hovered is { } hit && ReferenceEquals(hit.Component, component), destination);
            }
        }
        var children = node.Children;
        for (var childIndex = 0; childIndex < children.Count; childIndex++)
            BuildNode(children[childIndex], selectedNode, hovered, destination);
    }

    /// <summary>Gets a session-stable identity for one authored object.</summary>
    /// <param name="value">Node or component instance.</param>
    /// <returns>Identity retained while this registry is alive.</returns>
    private ulong GetPickingId(object value)
    {
        if (_pickingIds.TryGetValue(value, out var id))
            return id;
        id = _nextPickingId++;
        _pickingIds.Add(value, id);
        return id;
    }

    /// <summary>Invokes matching visible providers for one authored value.</summary>
    /// <param name="node">Owning transformed node.</param>
    /// <param name="value">Node or component value.</param>
    /// <param name="pickingId">Stable preview identity.</param>
    /// <param name="selected">Whether the node is selected.</param>
    /// <param name="hovered">Whether this exact node or component is hovered.</param>
    /// <param name="destination">Reusable output list.</param>
    private void BuildValue(
        Node3D node,
        object value,
        ScenePreviewPickingId pickingId,
        bool selected,
        bool hovered,
        ScenePreviewList destination)
    {
        if (_hiddenValues.Contains(value))
            return;
        for (var index = 0; index < _providers.Count; index++)
        {
            var provider = _providers[index];
            if (_categoryVisibility[(int)provider.Category] && provider.Supports(value))
                provider.Build(node, value, pickingId, selected, hovered, destination);
        }
    }

    /// <summary>Creates the built-in provider set shared by Scene viewport instances.</summary>
    /// <returns>A registry configured for nodes, cameras, and colliders.</returns>
    public static ScenePreviewRegistry CreateDefault(
        Func<AssetReference, StaticMeshResource?>? meshResolver = null,
        Func<AssetReference, TerrainResource?>? terrainResolver = null)
    {
        var registry = new ScenePreviewRegistry();
        registry.Register(new EmptyNodePreviewProvider());
        registry.Register(new CameraPreviewProvider());
        registry.Register(new DirectionalLightPreviewProvider());
        registry.Register(new ColliderPreviewProvider(meshResolver, terrainResolver));
        return registry;
    }
}

/// <summary>Draws an origin cross for Node3D objects without ordinary mesh geometry.</summary>
internal sealed class EmptyNodePreviewProvider : IScenePreviewProvider
{
    /// <inheritdoc/>
    public ScenePreviewCategory Category => ScenePreviewCategory.Nodes;

    /// <inheritdoc/>
    public bool Supports(object value) => value is Node3D and not MeshInstance3D and
        not PerspectiveCamera and not DirectionalLight3D;

    /// <inheritdoc/>
    public void Build(Node3D node, object value, ScenePreviewPickingId pickingId,
        bool selected, bool hovered, ScenePreviewList destination)
    {
        var origin = node.GetWorldPosition();
        var color = selected ? new Vector4(1f, 0.75f, 0.15f, 1f) :
            hovered ? new Vector4(0.7f, 0.88f, 1f, 1f) : new Vector4(0.45f, 0.7f, 1f, 0.8f);
        destination.AddIcon(new ScenePreviewIcon(origin, 16f,
            ScenePreviewIconKind.Origin, color, ScenePreviewDepthMode.AlwaysVisible, pickingId));
        AddCross(destination, origin, 0.2f, color, pickingId);
    }

    /// <summary>Adds a three-axis always-visible origin marker.</summary>
    /// <param name="destination">Primitive destination.</param>
    /// <param name="origin">Marker center.</param>
    /// <param name="radius">Half extent.</param>
    /// <param name="color">Marker color.</param>
    /// <param name="pickingId">Marker identity.</param>
    private static void AddCross(ScenePreviewList destination, Vector3 origin, float radius,
        Vector4 color, ScenePreviewPickingId pickingId)
    {
        destination.AddLine(new(origin - Vector3.UnitX * radius, origin + Vector3.UnitX * radius,
            color, ScenePreviewDepthMode.AlwaysVisible, pickingId));
        destination.AddLine(new(origin - Vector3.UnitY * radius, origin + Vector3.UnitY * radius,
            color, ScenePreviewDepthMode.AlwaysVisible, pickingId));
        destination.AddLine(new(origin - Vector3.UnitZ * radius, origin + Vector3.UnitZ * radius,
            color, ScenePreviewDepthMode.AlwaysVisible, pickingId));
    }
}
