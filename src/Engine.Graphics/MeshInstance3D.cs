using Engine.Core;
using System.Numerics;

namespace Engine.Graphics;

/// <summary>Describes renderer-independent object-space mesh bounds.</summary>
/// <param name="Minimum">Minimum object-space corner.</param>
/// <param name="Maximum">Maximum object-space corner.</param>
public readonly record struct MeshBounds(Vector3 Minimum, Vector3 Maximum);

/// <summary>
/// A 3D node that renders a Mesh. Combines a Node3D transform with a Mesh resource.
/// </summary>
public class MeshInstance3D : Node3D
{
    /// <summary>Gets or sets the persistent mesh resource to render.</summary>
    public AssetReference Mesh { get; set; }

    /// <summary>Gets or sets decoded object-space bounds used by editor picking.</summary>
    public MeshBounds? LocalBounds { get; set; }

    /// <summary>Gets persistent material assignments by mesh material slot.</summary>
    public List<AssetReference> Materials { get; } = [];

    /// <summary>Gets or sets the scene-local copy-on-write material override for slot zero.</summary>
    public MaterialProperties? MaterialOverride { get; set; }

    /// <summary>
    /// Creates a mesh instance using the built-in cube resource.
    /// </summary>
    public MeshInstance3D()
    {
        Mesh = BuiltInAssets.CubeMesh;
        Name = "MeshInstance3D";
    }
}

/// <summary>Defines stable engine-owned resources available without project imports.</summary>
public static class BuiltInAssets
{
    private static readonly AssetId EngineAsset = new(
        new Guid("00000000-0000-7000-8000-000000000001"));
    private static readonly Lazy<StaticMeshResource> CubeResource = new(
        () => BuiltInForwardMeshBuilder.BuildStaticMesh(new CubeMesh()));

    /// <summary>Gets the built-in unit cube mesh reference.</summary>
    public static AssetReference CubeMesh { get; } = new(EngineAsset, "mesh/cube");

    /// <summary>Gets whether a reference identifies the built-in unit cube.</summary>
    /// <param name="reference">Mesh resource reference.</param>
    /// <returns>True for the built-in cube.</returns>
    public static bool IsCubeMesh(AssetReference reference) => reference == CubeMesh;

    /// <summary>Creates decoded static geometry for a built-in mesh resource.</summary>
    /// <param name="reference">Built-in mesh reference.</param>
    /// <returns>Decoded static mesh geometry.</returns>
    public static StaticMeshResource LoadMesh(AssetReference reference)
    {
        if (!IsCubeMesh(reference))
            throw new KeyNotFoundException($"Built-in mesh '{reference}' was not found.");
        return CubeResource.Value;
    }
}
