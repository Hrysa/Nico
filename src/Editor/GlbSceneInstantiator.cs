using System.Numerics;
using Engine.Assets;
using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Creates an editable scene hierarchy from imported GLB node metadata.</summary>
public static class GlbSceneInstantiator
{
    /// <summary>Creates one model root and all mesh instances described by an import outcome.</summary>
    /// <param name="sourcePath">Physical GLB source path.</param>
    /// <param name="assetId">Persistent source asset identity.</param>
    /// <param name="outcome">Successful GLB import outcome.</param>
    /// <returns>The model root and its renderable mesh instances.</returns>
    public static GlbSceneInstantiation Create(
        string sourcePath,
        AssetId assetId,
        AssetImportOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(outcome);
        if (!outcome.Succeeded)
            throw new ArgumentException("A successful GLB import outcome is required.", nameof(outcome));

        var modelRoot = new Node3D { Name = Path.GetFileNameWithoutExtension(sourcePath) };
        var objects = outcome.Objects;
        if (objects is null)
            return new GlbSceneInstantiation(modelRoot, []);

        var sourceNodes = new List<AssetImportObject>();
        for (var index = 0; index < objects.Count; index++)
        {
            if (objects[index].Kind is "node" or "collision")
                sourceNodes.Add(objects[index]);
        }
        var sceneNodes = new Dictionary<string, Node3D>(sourceNodes.Count, StringComparer.Ordinal);
        for (var index = 0; index < sourceNodes.Count; index++)
        {
            var source = sourceNodes[index];
            var node = new Node3D { Name = source.Name };
            ApplyTransform(node, source.LocalTransform ?? Matrix4x4.Identity);
            sceneNodes.Add(source.Key, node);
        }
        for (var index = 0; index < sourceNodes.Count; index++)
        {
            var source = sourceNodes[index];
            var node = sceneNodes[source.Key];
            if (source.ParentKey is not null &&
                sceneNodes.TryGetValue(source.ParentKey, out var parent))
            {
                parent.AddChild(node);
            }
            else
            {
                modelRoot.AddChild(node);
            }
        }

        var meshes = new List<MeshInstance3D>();
        AddStaticModelBatches(modelRoot, assetId, outcome.Artifacts, meshes);
        var includeStaticNodeMeshes = meshes.Count == 0;
        for (var index = 0; index < sourceNodes.Count; index++)
            AddMeshes(sourceNodes[index], sceneNodes[sourceNodes[index].Key], modelRoot,
                assetId, outcome.Artifacts, meshes, includeStaticNodeMeshes);
        return new GlbSceneInstantiation(modelRoot, meshes);
    }

    /// <summary>Adds optimized world-baked static model batches when supplied by the importer.</summary>
    /// <param name="modelRoot">Created model root.</param>
    /// <param name="assetId">Persistent source identity.</param>
    /// <param name="artifacts">Published import artifacts.</param>
    /// <param name="meshes">Destination renderable list.</param>
    private static void AddStaticModelBatches(
        Node3D modelRoot,
        AssetId assetId,
        IReadOnlyList<AssetArtifact> artifacts,
        List<MeshInstance3D> meshes)
    {
        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            if (artifact.ContentType != "nico/static-mesh" ||
                !artifact.Key.StartsWith("model-batch/", StringComparison.Ordinal))
            {
                continue;
            }
            var mesh = new MeshInstance3D
            {
                Name = $"Static Batch {artifact.Key["model-batch/".Length..]}",
                Mesh = new AssetReference(assetId, artifact.Key)
            };
            modelRoot.AddChild(mesh);
            meshes.Add(mesh);
        }
    }

    /// <summary>Adds all primitive artifacts attached to one imported node.</summary>
    /// <param name="source">Imported node description.</param>
    /// <param name="parent">Created scene node.</param>
    /// <param name="modelRoot">Created model root.</param>
    /// <param name="assetId">Persistent source identity.</param>
    /// <param name="artifacts">Published import artifacts.</param>
    /// <param name="meshes">Destination renderable list.</param>
    /// <param name="includeStaticMeshes">Whether unbatched static primitives are required.</param>
    private static void AddMeshes(
        AssetImportObject source,
        Node3D parent,
        Node3D modelRoot,
        AssetId assetId,
        IReadOnlyList<AssetArtifact> artifacts,
        List<MeshInstance3D> meshes,
        bool includeStaticMeshes)
    {
        if (source.ArtifactKeys is not { Count: > 0 } keys)
            return;
        for (var keyIndex = 0; keyIndex < keys.Count; keyIndex++)
        {
            AssetArtifact? artifact = null;
            for (var artifactIndex = 0; artifactIndex < artifacts.Count; artifactIndex++)
            {
                if (artifacts[artifactIndex].Key == keys[keyIndex])
                {
                    artifact = artifacts[artifactIndex];
                    break;
                }
            }
            if (artifact is null ||
                artifact.ContentType is not "nico/static-mesh" and not "nico/skinned-mesh")
            {
                continue;
            }
            if (source.Kind == "collision")
            {
                if (artifact.ContentType != "nico/static-mesh")
                    throw new InvalidDataException("Collision source nodes must use static triangle meshes.");
                parent.AddComponent(new MeshColliderComponent
                {
                    Mesh = new AssetReference(assetId, artifact.Key)
                });
                continue;
            }
            if (!includeStaticMeshes && artifact.ContentType == "nico/static-mesh")
                continue;
            var mesh = new MeshInstance3D
            {
                Name = keys.Count == 1 ? source.Name : $"{source.Name} {keyIndex + 1}",
                Mesh = new AssetReference(assetId, artifact.Key)
            };
            if (artifact.ContentType == "nico/skinned-mesh")
            {
                // The skinned resource already contains its source mesh world transform so
                // inheriting the imported visual-node chain would apply it twice.
                modelRoot.AddChild(mesh);
            }
            else
            {
                parent.AddChild(mesh);
            }
            meshes.Add(mesh);
        }
    }

    /// <summary>Decomposes and assigns one parent-relative transform.</summary>
    /// <param name="node">Destination scene node.</param>
    /// <param name="transform">Parent-relative source transform.</param>
    private static void ApplyTransform(Node3D node, Matrix4x4 transform)
    {
        if (!Matrix4x4.Decompose(transform, out var scale, out var orientation,
                out var translation) || !IsFinite(scale) || !IsFinite(translation) ||
            !IsFinite(orientation))
        {
            throw new InvalidDataException($"GLB node '{node.Name}' has a non-decomposable transform.");
        }
        node.Position = translation;
        node.Orientation = orientation;
        node.Scale = scale;
    }

    /// <summary>Checks whether all vector components are finite.</summary>
    /// <param name="value">Vector to check.</param>
    /// <returns>True when all components are finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Checks whether all quaternion components are finite.</summary>
    /// <param name="value">Quaternion to check.</param>
    /// <returns>True when all components are finite.</returns>
    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}

/// <summary>Contains a created GLB model root and its flattened renderable list.</summary>
/// <param name="Root">Created model hierarchy root.</param>
/// <param name="Meshes">All mesh instances contained by the hierarchy.</param>
public sealed record GlbSceneInstantiation(
    Node3D Root,
    IReadOnlyList<MeshInstance3D> Meshes);
