using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Identifies a diagnostic Scene viewport preview category.</summary>
public enum ScenePreviewCategory
{
    /// <summary>Transform markers for otherwise invisible nodes.</summary>
    Nodes,
    /// <summary>Camera icons, directions, and frustums.</summary>
    Cameras,
    /// <summary>Light origins and illumination directions.</summary>
    Lights,
    /// <summary>Authored collision geometry.</summary>
    Colliders
}

/// <summary>Controls whether diagnostic geometry participates in depth testing.</summary>
public enum ScenePreviewDepthMode
{
    /// <summary>Geometry is obscured by ordinary scene geometry.</summary>
    DepthTested,
    /// <summary>Geometry remains visible through ordinary scene geometry.</summary>
    AlwaysVisible
}

/// <summary>Maps one preview primitive back to authored scene content.</summary>
/// <param name="Value">Stable identifier within the current scene hierarchy.</param>
/// <param name="Node">Owning scene node.</param>
/// <param name="Component">Optional owning component.</param>
public readonly record struct ScenePreviewPickingId(
    ulong Value,
    Node3D Node,
    Component? Component = null);

/// <summary>Describes one colored world-space diagnostic line.</summary>
/// <param name="Start">World-space start point.</param>
/// <param name="End">World-space end point.</param>
/// <param name="Color">Linear RGBA color.</param>
/// <param name="DepthMode">Requested depth behavior.</param>
/// <param name="PickingId">Owning preview identifier.</param>
public readonly record struct ScenePreviewLine(
    Vector3 Start,
    Vector3 End,
    Vector4 Color,
    ScenePreviewDepthMode DepthMode,
    ScenePreviewPickingId PickingId);

/// <summary>Identifies a renderer-independent diagnostic icon.</summary>
public enum ScenePreviewIconKind
{
    /// <summary>Generic transform origin.</summary>
    Origin,
    /// <summary>Perspective camera body.</summary>
    Camera,
    /// <summary>Directional-light origin.</summary>
    Light,
    /// <summary>Invalid or unresolved authored data.</summary>
    Warning
}

/// <summary>Describes one world-anchored editor icon.</summary>
/// <param name="Position">World anchor.</param><param name="Size">Logical icon size.</param>
/// <param name="Kind">Semantic icon kind.</param><param name="Color">Linear RGBA color.</param>
/// <param name="DepthMode">Depth behavior.</param><param name="PickingId">Owner identity.</param>
public readonly record struct ScenePreviewIcon(Vector3 Position, float Size,
    ScenePreviewIconKind Kind, Vector4 Color, ScenePreviewDepthMode DepthMode,
    ScenePreviewPickingId PickingId);

/// <summary>Describes a semantic perspective frustum diagnostic.</summary>
/// <param name="Transform">Camera world transform.</param><param name="Fov">Vertical field of view.</param>
/// <param name="Aspect">Width divided by height.</param><param name="Near">Near distance.</param>
/// <param name="Far">Far distance.</param><param name="Color">Linear RGBA color.</param>
/// <param name="DepthMode">Depth behavior.</param><param name="PickingId">Owner identity.</param>
public readonly record struct ScenePreviewFrustum(Matrix4x4 Transform, float Fov,
    float Aspect, float Near, float Far, Vector4 Color, ScenePreviewDepthMode DepthMode,
    ScenePreviewPickingId PickingId);

/// <summary>Describes transformed diagnostic bounds.</summary>
/// <param name="Transform">Local-to-world transform.</param><param name="Bounds">Local bounds.</param>
/// <param name="Color">Linear RGBA color.</param><param name="DepthMode">Depth behavior.</param>
/// <param name="PickingId">Owner identity.</param>
public readonly record struct ScenePreviewBounds(Matrix4x4 Transform, MeshBounds Bounds,
    Vector4 Color, ScenePreviewDepthMode DepthMode, ScenePreviewPickingId PickingId);

/// <summary>Describes an explicit triangle resource rendered as diagnostic wire geometry.</summary>
/// <param name="Mesh">Cached renderer-independent mesh resource.</param>
/// <param name="Transform">Local-to-world transform.</param><param name="Color">Linear RGBA color.</param>
/// <param name="DepthMode">Depth behavior.</param><param name="PickingId">Owner identity.</param>
public readonly record struct ScenePreviewWireMesh(StaticMeshResource Mesh, Matrix4x4 Transform,
    Vector4 Color, ScenePreviewDepthMode DepthMode, ScenePreviewPickingId PickingId);

/// <summary>Describes an explicit triangle resource rendered with diagnostic translucency.</summary>
/// <param name="Mesh">Cached renderer-independent mesh resource.</param>
/// <param name="Transform">Local-to-world transform.</param><param name="Color">Linear RGBA color.</param>
/// <param name="DepthMode">Depth behavior.</param><param name="PickingId">Owner identity.</param>
public readonly record struct ScenePreviewTranslucentMesh(StaticMeshResource Mesh,
    Matrix4x4 Transform, Vector4 Color, ScenePreviewDepthMode DepthMode,
    ScenePreviewPickingId PickingId);

/// <summary>Reusable per-frame destination for renderer-independent Scene preview primitives.</summary>
public sealed class ScenePreviewList
{
    private readonly List<ScenePreviewLine> _lines = [];
    private readonly List<ScenePreviewIcon> _icons = [];
    private readonly List<ScenePreviewFrustum> _frustums = [];
    private readonly List<ScenePreviewBounds> _bounds = [];
    private readonly List<ScenePreviewWireMesh> _wireMeshes = [];
    private readonly List<ScenePreviewTranslucentMesh> _translucentMeshes = [];

    /// <summary>Gets authored world-space line primitives without exposing mutation.</summary>
    public IReadOnlyList<ScenePreviewLine> Lines => _lines;

    /// <summary>Gets semantic world-anchored icons.</summary>
    public IReadOnlyList<ScenePreviewIcon> Icons => _icons;

    /// <summary>Gets semantic camera frustums.</summary>
    public IReadOnlyList<ScenePreviewFrustum> Frustums => _frustums;

    /// <summary>Gets transformed bounds diagnostics.</summary>
    public IReadOnlyList<ScenePreviewBounds> Bounds => _bounds;

    /// <summary>Gets explicit cached wire meshes.</summary>
    public IReadOnlyList<ScenePreviewWireMesh> WireMeshes => _wireMeshes;

    /// <summary>Gets explicit cached translucent meshes.</summary>
    public IReadOnlyList<ScenePreviewTranslucentMesh> TranslucentMeshes => _translucentMeshes;

    /// <summary>Removes primitives retained from the preceding frame while preserving capacity.</summary>
    public void Clear()
    {
        _lines.Clear();
        _icons.Clear();
        _frustums.Clear();
        _bounds.Clear();
        _wireMeshes.Clear();
        _translucentMeshes.Clear();
    }

    /// <summary>Adds one world-space line primitive.</summary>
    /// <param name="line">Line to append.</param>
    public void AddLine(ScenePreviewLine line)
    {
        _lines.Add(line);
    }

    /// <summary>Adds one semantic icon.</summary>
    /// <param name="icon">Icon to append.</param>
    public void AddIcon(ScenePreviewIcon icon) => _icons.Add(icon);

    /// <summary>Adds one semantic camera frustum.</summary>
    /// <param name="frustum">Frustum to append.</param>
    public void AddFrustum(ScenePreviewFrustum frustum) => _frustums.Add(frustum);

    /// <summary>Adds one transformed bounds diagnostic.</summary>
    /// <param name="bounds">Bounds to append.</param>
    public void AddBounds(ScenePreviewBounds bounds) => _bounds.Add(bounds);

    /// <summary>Adds one explicit wire mesh diagnostic.</summary>
    /// <param name="mesh">Wire mesh to append.</param>
    public void AddWireMesh(ScenePreviewWireMesh mesh) => _wireMeshes.Add(mesh);

    /// <summary>Adds one explicit translucent mesh diagnostic.</summary>
    /// <param name="mesh">Translucent mesh to append.</param>
    public void AddTranslucentMesh(ScenePreviewTranslucentMesh mesh) =>
        _translucentMeshes.Add(mesh);
}
