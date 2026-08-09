using System.Numerics;
using System.Text;
using System.Text.Json;
using SharpGLTF.Schema2;
using StbImageSharp;

namespace Engine.Assets;

/// <summary>Imports static or skinned triangle primitives from a GLB 2.0 source.</summary>
public sealed class GlbModelImporter : IAssetImporter
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinaryChunk = 0x004E4942;

    /// <inheritdoc/>
    public string Id => "gltf-model";

    /// <inheritdoc/>
    public int Version => 6;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ModelRoot model;
        using (var validationSource = context.OpenSource())
            model = ModelRoot.ReadGLB(validationSource);
        using var source = context.OpenSource();
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        var (document, binary) = ReadContainer(reader);
        using (document)
        {
            var hasMeshes = document.RootElement.TryGetProperty("meshes", out _);
            var artifacts = ImportMeshes(context, document.RootElement, binary, model).ToList();
            if (!hasMeshes)
                artifacts.AddRange(ImportStandaloneAnimations(
                    context, document.RootElement, binary, model));
            artifacts.AddRange(ImportMaterials(context, document.RootElement));
            artifacts.AddRange(ImportTextures(context, document.RootElement, binary));
            var objects = ImportObjects(document.RootElement, animationsAreArtifacts: !hasMeshes);
            return new AssetImportResult(artifacts, [], [], objects);
        }
    }

    /// <summary>Describes browsable nodes, skeletons, and animations contained in a GLB.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="animationsAreArtifacts">Whether animation objects represent artifacts.</param>
    /// <returns>Stable source-object descriptions for editor browsing.</returns>
    private static IReadOnlyList<AssetImportObject> ImportObjects(
        JsonElement root,
        bool animationsAreArtifacts)
    {
        var objects = new List<AssetImportObject>();
        if (root.TryGetProperty("nodes", out var nodes))
        {
            var parents = BuildNodeParents(nodes);
            for (var nodeIndex = 0; nodeIndex < nodes.GetArrayLength(); nodeIndex++)
            {
                var node = nodes[nodeIndex];
                var name = node.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() : null;
                objects.Add(new AssetImportObject(
                    $"node/{nodeIndex}",
                    string.IsNullOrWhiteSpace(name) ? $"Node {nodeIndex}" : name,
                    "node",
                    parents[nodeIndex] < 0 ? null : $"node/{parents[nodeIndex]}"));
            }
        }
        if (root.TryGetProperty("skins", out var skins))
        {
            for (var skinIndex = 0; skinIndex < skins.GetArrayLength(); skinIndex++)
            {
                var skin = skins[skinIndex];
                var name = skin.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() : null;
                objects.Add(new AssetImportObject(
                    $"skeleton/{skinIndex}",
                    string.IsNullOrWhiteSpace(name) ? $"Skeleton {skinIndex}" : name,
                    "skeleton"));
            }
        }
        if (root.TryGetProperty("animations", out var animations))
        {
            for (var animationIndex = 0;
                 animationIndex < animations.GetArrayLength(); animationIndex++)
            {
                var animation = animations[animationIndex];
                var name = animation.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() : null;
                objects.Add(new AssetImportObject(
                    $"animation/{animationIndex}",
                    string.IsNullOrWhiteSpace(name) ? $"Animation {animationIndex}" : name,
                    "animation",
                    ArtifactKey: animationsAreArtifacts ? $"animation/{animationIndex}" : null));
            }
        }
        return objects;
    }

    /// <summary>Reads and validates the GLB header and required chunks.</summary>
    /// <param name="reader">Source binary reader.</param>
    /// <returns>Parsed JSON and binary buffer.</returns>
    private static (JsonDocument Document, byte[] Binary) ReadContainer(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 20 || reader.ReadUInt32() != GlbMagic)
            throw new InvalidDataException("Source is not a GLB container.");
        if (reader.ReadUInt32() != 2)
            throw new InvalidDataException("Only GLB version 2 is supported.");
        var declaredLength = reader.ReadUInt32();
        if (declaredLength != reader.BaseStream.Length)
            throw new InvalidDataException("GLB declared length does not match the source file.");
        JsonDocument? document = null;
        byte[] binary = [];
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var length = reader.ReadUInt32();
            var type = reader.ReadUInt32();
            if (length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("GLB chunk exceeds the source file.");
            var bytes = reader.ReadBytes(checked((int)length));
            if (type == JsonChunk && document is null)
                document = JsonDocument.Parse(bytes);
            else if (type == BinaryChunk && binary.Length == 0)
                binary = bytes;
        }
        return (document ?? throw new InvalidDataException("GLB has no JSON chunk."), binary);
    }

    /// <summary>Imports every triangle primitive as an independently addressable mesh.</summary>
    /// <param name="context">Artifact output context.</param>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="model">Validated SharpGLTF model used to evaluate animation.</param>
    /// <returns>Published mesh artifacts.</returns>
    private static IReadOnlyList<AssetArtifact> ImportMeshes(
        AssetImportContext context,
        JsonElement root,
        byte[] binary,
        ModelRoot model)
    {
        if (!root.TryGetProperty("asset", out var asset) ||
            !asset.TryGetProperty("version", out var version) ||
            version.GetString() is not { } versionText || !versionText.StartsWith("2", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Only glTF 2.x assets are supported.");
        }
        if (!root.TryGetProperty("meshes", out var meshes))
            return [];
        var artifacts = new List<AssetArtifact>();
        var meshIndex = 0;
        foreach (var mesh in meshes.EnumerateArray())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var meshName = mesh.TryGetProperty("name", out var name)
                ? Sanitize(name.GetString(), $"mesh-{meshIndex}") : $"mesh-{meshIndex}";
            var skin = FindSkinForMesh(root, meshIndex);
            ImportedSkeleton? skeleton = skin is null
                ? null : ImportSkeleton(root, binary, skin.Value.Skin,
                    skin.Value.MeshNodeIndex);
            var animations = skeleton is null
                ? [] : ImportAnimations(model, skeleton.Value);
            var primitiveIndex = 0;
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var mode = primitive.TryGetProperty("mode", out var modeElement)
                    ? modeElement.GetInt32() : 4;
                if (mode != 4)
                    throw new InvalidDataException("Only triangle-list GLB primitives are supported.");
                var attributes = primitive.GetProperty("attributes");
                var positions = ReadVector3(root, binary,
                    attributes.GetProperty("POSITION").GetInt32(), "POSITION");
                var normals = attributes.TryGetProperty("NORMAL", out var normalAccessor)
                    ? ReadVector3(root, binary, normalAccessor.GetInt32(), "NORMAL")
                    : new Vector3[positions.Length];
                var texCoords = attributes.TryGetProperty("TEXCOORD_0", out var uvAccessor)
                    ? ReadVector2(root, binary, uvAccessor.GetInt32(), "TEXCOORD_0")
                    : new Vector2[positions.Length];
                var tangents = attributes.TryGetProperty("TANGENT", out var tangentAccessor)
                    ? ReadVector4(root, binary, tangentAccessor.GetInt32(), "TANGENT")
                    : Enumerable.Repeat(new Vector4(1f, 0f, 0f, 1f), positions.Length).ToArray();
                var colors = attributes.TryGetProperty("COLOR_0", out var colorAccessor)
                    ? ReadColors(root, binary, colorAccessor.GetInt32())
                    : Enumerable.Repeat(Vector4.One, positions.Length).ToArray();
                if (normals.Length != positions.Length || texCoords.Length != positions.Length ||
                    tangents.Length != positions.Length || colors.Length != positions.Length)
                {
                    throw new InvalidDataException("GLB vertex attribute counts do not match POSITION.");
                }
                var indices = primitive.TryGetProperty("indices", out var indexAccessor)
                    ? ReadIndices(root, binary, indexAccessor.GetInt32())
                    : Enumerable.Range(0, positions.Length).Select(index => checked((uint)index)).ToArray();
                if (indices.Length % 3 != 0 || indices.Any(index => index >= positions.Length))
                    throw new InvalidDataException("GLB primitive contains invalid triangle indices.");
                if (!attributes.TryGetProperty("NORMAL", out _))
                    GenerateNormals(positions, indices, normals);
                if (skeleton is null)
                {
                    var nodeTransform = FindStaticMeshNodeTransform(root, meshIndex);
                    ApplyStaticMeshTransform(
                        positions, normals, tangents, indices, nodeTransform);
                }
                var materialSlot = primitive.TryGetProperty("material", out var materialElement)
                    ? materialElement.GetInt32() : -1;
                var relativePath = skeleton is null
                    ? $"meshes/{meshName}-{primitiveIndex}.nmesh"
                    : $"meshes/{meshName}-{primitiveIndex}.nskin";
                using (var output = context.CreateArtifact(relativePath))
                {
                    if (skeleton is null)
                    {
                        WriteMesh(output, positions, normals, texCoords, tangents, colors, indices,
                            materialSlot);
                    }
                    else
                    {
                        if (!attributes.TryGetProperty("JOINTS_0", out var jointsAccessor) ||
                            !attributes.TryGetProperty("WEIGHTS_0", out var weightsAccessor))
                        {
                            throw new InvalidDataException(
                                "A GLB mesh referenced by a skin requires JOINTS_0 and WEIGHTS_0.");
                        }
                        var joints = ReadJointIndices(root, binary, jointsAccessor.GetInt32(),
                            skeleton.Value.SourceJointToSkeletonJoint);
                        var weights = ReadWeights(root, binary, weightsAccessor.GetInt32());
                        if (joints.Length != positions.Length || weights.Length != positions.Length)
                            throw new InvalidDataException(
                                "GLB skin attribute counts do not match POSITION.");
                        WriteSkinnedMesh(output, positions, normals, texCoords, tangents, colors,
                            joints, weights, indices, materialSlot, skeleton.Value, animations);
                    }
                }
                artifacts.Add(new AssetArtifact(
                    $"mesh/{meshName}/{primitiveIndex}",
                    skeleton is null ? "nico/static-mesh" : "nico/skinned-mesh",
                    relativePath));
                primitiveIndex++;
            }
            meshIndex++;
        }
        if (artifacts.Count == 0)
            throw new InvalidDataException("GLB contains no mesh primitives.");
        return artifacts;
    }

    /// <summary>Imports clips from a GLB that intentionally contains no geometry.</summary>
    /// <param name="context">Artifact output context.</param>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="model">Validated SharpGLTF model used to evaluate animation.</param>
    /// <returns>One independently addressable artifact per imported clip.</returns>
    private static IReadOnlyList<AssetArtifact> ImportStandaloneAnimations(
        AssetImportContext context,
        JsonElement root,
        byte[] binary,
        ModelRoot model)
    {
        if (!root.TryGetProperty("skins", out var skins) || skins.GetArrayLength() == 0 ||
            model.LogicalAnimations.Count == 0)
        {
            throw new InvalidDataException(
                "A mesh-free GLB must contain a skin and at least one skeletal animation.");
        }
        if (skins.GetArrayLength() != 1)
        {
            throw new InvalidDataException(
                "Animation-only GLBs containing multiple skins are not supported.");
        }
        if (!root.TryGetProperty("nodes", out var nodes))
            throw new InvalidDataException("GLB skin requires nodes.");
        var skin = skins[0];
        var skeletonRoot = skin.TryGetProperty("skeleton", out var rootElement)
            ? rootElement.GetInt32()
            : skin.GetProperty("joints")[0].GetInt32();
        if ((uint)skeletonRoot >= nodes.GetArrayLength())
            throw new InvalidDataException("GLB skeleton root index is out of range.");
        var referenceNodeIndex = BuildNodeParents(nodes)[skeletonRoot];
        var skeleton = ImportSkeleton(root, binary, skin, referenceNodeIndex);
        var animations = ImportAnimations(model, skeleton);
        var artifacts = new List<AssetArtifact>(animations.Length);
        for (var animationIndex = 0; animationIndex < animations.Length; animationIndex++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var name = Sanitize(animations[animationIndex].Name,
                $"animation-{animationIndex}");
            var relativePath = $"animations/{name}-{animationIndex}.nanim";
            using (var output = context.CreateArtifact(relativePath))
                WriteSkeletalAnimation(output, skeleton, animations[animationIndex]);
            artifacts.Add(new AssetArtifact(
                $"animation/{animationIndex}", "nico/skeletal-animation", relativePath));
        }
        return artifacts;
    }

    /// <summary>Finds the single skin used by nodes instantiating one mesh.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="meshIndex">Logical mesh index.</param>
    /// <returns>The skin and instantiating node, or null for a static mesh.</returns>
    private static SkinBinding? FindSkinForMesh(JsonElement root, int meshIndex)
    {
        if (!root.TryGetProperty("nodes", out var nodes) ||
            !root.TryGetProperty("skins", out var skins))
        {
            return null;
        }
        int? selectedSkin = null;
        var selectedNode = -1;
        var nodeIndex = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("mesh", out var mesh) || mesh.GetInt32() != meshIndex ||
                !node.TryGetProperty("skin", out var skin))
            {
                nodeIndex++;
                continue;
            }
            var skinIndex = skin.GetInt32();
            if ((uint)skinIndex >= skins.GetArrayLength())
                throw new InvalidDataException("GLB node skin index is out of range.");
            if (selectedSkin is not null && selectedSkin != skinIndex)
            {
                throw new InvalidDataException(
                    "One GLB mesh cannot be imported with multiple different skins.");
            }
            if (selectedNode >= 0)
                throw new InvalidDataException(
                    "One skinned GLB mesh cannot be instantiated by multiple nodes.");
            selectedSkin = skinIndex;
            selectedNode = nodeIndex;
            nodeIndex++;
        }
        return selectedSkin is null
            ? null : new SkinBinding(skins[selectedSkin.Value], selectedNode);
    }

    /// <summary>Finds the shared world transform of nodes instantiating one static mesh.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="meshIndex">Logical mesh index.</param>
    /// <returns>The unique world transform, or identity when the mesh has no unique instance.</returns>
    private static Matrix4x4 FindStaticMeshNodeTransform(JsonElement root, int meshIndex)
    {
        if (!root.TryGetProperty("nodes", out var nodes))
            return Matrix4x4.Identity;
        var parents = BuildNodeParents(nodes);
        var cache = new Matrix4x4[nodes.GetArrayLength()];
        var states = new byte[nodes.GetArrayLength()];
        Matrix4x4? selected = null;
        for (var nodeIndex = 0; nodeIndex < nodes.GetArrayLength(); nodeIndex++)
        {
            var node = nodes[nodeIndex];
            if (!node.TryGetProperty("mesh", out var mesh) || mesh.GetInt32() != meshIndex ||
                node.TryGetProperty("skin", out _))
            {
                continue;
            }
            var world = ComputeNodeWorldMatrix(nodes, parents, nodeIndex, cache, states);
            if (selected is { } existing && existing != world)
                return Matrix4x4.Identity;
            selected = world;
        }
        return selected ?? Matrix4x4.Identity;
    }

    /// <summary>Bakes one static mesh node transform into its vertex attributes.</summary>
    /// <param name="positions">Mutable object-space positions.</param>
    /// <param name="normals">Mutable object-space normals.</param>
    /// <param name="tangents">Mutable object-space tangents.</param>
    /// <param name="indices">Mutable triangle indices.</param>
    /// <param name="transform">Node world transform to bake.</param>
    private static void ApplyStaticMeshTransform(
        Vector3[] positions,
        Vector3[] normals,
        Vector4[] tangents,
        uint[] indices,
        Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
            return;
        if (!Matrix4x4.Invert(transform, out var inverse))
            throw new InvalidDataException("GLB static mesh node transform is not invertible.");
        var normalTransform = Matrix4x4.Transpose(inverse);
        var determinant = transform.GetDeterminant();
        for (var index = 0; index < positions.Length; index++)
        {
            positions[index] = Vector3.Transform(positions[index], transform);
            var normal = Vector3.TransformNormal(normals[index], normalTransform);
            normals[index] = normal.LengthSquared() > 0f
                ? Vector3.Normalize(normal) : Vector3.UnitY;
            var tangent = Vector3.TransformNormal(
                new Vector3(tangents[index].X, tangents[index].Y, tangents[index].Z),
                transform);
            tangent = tangent.LengthSquared() > 0f ? Vector3.Normalize(tangent) : Vector3.UnitX;
            tangents[index] = new Vector4(
                tangent, determinant < 0f ? -tangents[index].W : tangents[index].W);
        }
        if (determinant >= 0f)
            return;
        for (var index = 0; index < indices.Length; index += 3)
            (indices[index + 1], indices[index + 2]) = (indices[index + 2], indices[index + 1]);
    }

    /// <summary>Imports and topologically orders joints for one glTF skin.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="skin">Skin JSON.</param>
    /// <param name="meshNodeIndex">Reference node for skeleton-relative transforms, or -1.</param>
    /// <returns>Imported skeleton and source-index mappings.</returns>
    private static ImportedSkeleton ImportSkeleton(
        JsonElement root,
        byte[] binary,
        JsonElement skin,
        int meshNodeIndex)
    {
        if (!root.TryGetProperty("nodes", out var nodes) ||
            !skin.TryGetProperty("joints", out var jointElements) ||
            jointElements.GetArrayLength() == 0)
        {
            throw new InvalidDataException("GLB skin has no joints.");
        }
        var nodeCount = nodes.GetArrayLength();
        var parentByNode = BuildNodeParents(nodes);
        var sourceNodes = jointElements.EnumerateArray().Select(value => value.GetInt32()).ToArray();
        var sourceIndexByNode = Enumerable.Repeat(-1, nodeCount).ToArray();
        for (var index = 0; index < sourceNodes.Length; index++)
        {
            var nodeIndex = sourceNodes[index];
            if ((uint)nodeIndex >= nodeCount || sourceIndexByNode[nodeIndex] >= 0)
                throw new InvalidDataException("GLB skin contains an invalid or duplicate joint.");
            sourceIndexByNode[nodeIndex] = index;
        }
        var parentSourceIndices = new int[sourceNodes.Length];
        for (var index = 0; index < sourceNodes.Length; index++)
        {
            var parentNode = parentByNode[sourceNodes[index]];
            while (parentNode >= 0 && sourceIndexByNode[parentNode] < 0)
                parentNode = parentByNode[parentNode];
            parentSourceIndices[index] = parentNode < 0 ? -1 : sourceIndexByNode[parentNode];
        }
        var sourceToOrdered = Enumerable.Repeat(-1, sourceNodes.Length).ToArray();
        var orderedSources = new int[sourceNodes.Length];
        var orderedCount = 0;
        while (orderedCount < orderedSources.Length)
        {
            var progressed = false;
            for (var sourceIndex = 0; sourceIndex < sourceNodes.Length; sourceIndex++)
            {
                if (sourceToOrdered[sourceIndex] >= 0)
                    continue;
                var parent = parentSourceIndices[sourceIndex];
                if (parent >= 0 && sourceToOrdered[parent] < 0)
                    continue;
                sourceToOrdered[sourceIndex] = orderedCount;
                orderedSources[orderedCount++] = sourceIndex;
                progressed = true;
            }
            if (!progressed)
                throw new InvalidDataException("GLB skin joint hierarchy contains a cycle.");
        }
        Matrix4x4[] inverseBindBySource;
        if (skin.TryGetProperty("inverseBindMatrices", out var inverseBindAccessor))
        {
            inverseBindBySource = ReadMatrices(root, binary, inverseBindAccessor.GetInt32());
            if (inverseBindBySource.Length != sourceNodes.Length)
                throw new InvalidDataException("GLB inverse bind matrix count does not match joints.");
        }
        else
        {
            inverseBindBySource = new Matrix4x4[sourceNodes.Length];
            for (var sourceIndex = 0; sourceIndex < sourceNodes.Length; sourceIndex++)
                inverseBindBySource[sourceIndex] = Matrix4x4.Identity;
        }
        var worldCache = new Matrix4x4[nodeCount];
        var worldStates = new byte[nodeCount];
        var meshWorld = meshNodeIndex < 0
            ? Matrix4x4.Identity
            : ComputeNodeWorldMatrix(nodes, parentByNode, meshNodeIndex,
                worldCache, worldStates);
        if (!Matrix4x4.Invert(meshWorld, out var inverseMeshWorld))
            throw new InvalidDataException("GLB mesh bind transform is not invertible.");
        var globalBindBySource = new Matrix4x4[sourceNodes.Length];
        for (var sourceIndex = 0; sourceIndex < sourceNodes.Length; sourceIndex++)
        {
            globalBindBySource[sourceIndex] = ComputeNodeWorldMatrix(
                nodes, parentByNode, sourceNodes[sourceIndex], worldCache, worldStates) *
                inverseMeshWorld;
        }
        var joints = new ImportedJoint[sourceNodes.Length];
        var orderedNodeIndices = new int[sourceNodes.Length];
        for (var orderedIndex = 0; orderedIndex < orderedSources.Length; orderedIndex++)
        {
            var sourceIndex = orderedSources[orderedIndex];
            var parentSource = parentSourceIndices[sourceIndex];
            var local = globalBindBySource[sourceIndex];
            if (parentSource >= 0)
            {
                if (!Matrix4x4.Invert(globalBindBySource[parentSource], out var inverseParent))
                    throw new InvalidDataException("GLB parent bind transform is not invertible.");
                local *= inverseParent;
            }
            if (!Matrix4x4.Decompose(local, out var scale, out var rotation,
                out var translation))
            {
                throw new InvalidDataException("GLB joint bind transform cannot be decomposed.");
            }
            var nodeIndex = sourceNodes[sourceIndex];
            var node = nodes[nodeIndex];
            var name = node.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() : null;
            joints[orderedIndex] = new ImportedJoint(
                string.IsNullOrWhiteSpace(name) ? $"joint-{nodeIndex}" : name,
                parentSource < 0 ? -1 : sourceToOrdered[parentSource],
                translation,
                Quaternion.Normalize(rotation),
                scale,
                inverseBindBySource[sourceIndex]);
            orderedNodeIndices[orderedIndex] = nodeIndex;
        }
        return new ImportedSkeleton(
            joints, orderedNodeIndices, sourceToOrdered, meshNodeIndex, meshWorld);
    }

    /// <summary>Imports animation curves targeting joints in one skeleton.</summary>
    /// <param name="model">Validated SharpGLTF model.</param>
    /// <param name="skeleton">Imported skeleton mapping.</param>
    /// <returns>Imported clips.</returns>
    private static ImportedAnimation[] ImportAnimations(
        ModelRoot model,
        ImportedSkeleton skeleton)
    {
        var animations = model.LogicalAnimations;
        if (animations.Count == 0)
            return [];
        var result = new ImportedAnimation[animations.Count];
        for (var animationIndex = 0; animationIndex < animations.Count; animationIndex++)
        {
            var animation = animations[animationIndex];
            var duration = Math.Max(0f, animation.Duration);
            const float samplesPerSecond = 60f;
            var sampleCount = duration <= 0f
                ? 1 : checked((int)MathF.Ceiling(duration * samplesPerSecond) + 1);
            var times = new float[sampleCount];
            for (var sample = 0; sample < sampleCount; sample++)
                times[sample] = sample == sampleCount - 1
                    ? duration : sample / samplesPerSecond;
            var tracks = new ImportedJointTrack?[skeleton.Joints.Length];
            var meshNode = skeleton.MeshNodeIndex < 0
                ? null : model.LogicalNodes[skeleton.MeshNodeIndex];
            var worldMatrices = new Matrix4x4[skeleton.Joints.Length];
            var translations = new Vector3[skeleton.Joints.Length][];
            var rotations = new Vector4[skeleton.Joints.Length][];
            var scales = new Vector3[skeleton.Joints.Length][];
            for (var jointIndex = 0; jointIndex < skeleton.Joints.Length; jointIndex++)
            {
                translations[jointIndex] = new Vector3[sampleCount];
                rotations[jointIndex] = new Vector4[sampleCount];
                scales[jointIndex] = new Vector3[sampleCount];
            }
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var time = times[sample];
                var meshWorld = meshNode?.GetWorldMatrix(animation, time) ?? Matrix4x4.Identity;
                if (!Matrix4x4.Invert(meshWorld, out var inverseMeshWorld))
                    throw new InvalidDataException("Animated GLB mesh transform is not invertible.");
                for (var jointIndex = 0; jointIndex < skeleton.Joints.Length; jointIndex++)
                {
                    var node = model.LogicalNodes[skeleton.SourceNodeIndices[jointIndex]];
                    var world = node.GetWorldMatrix(animation, time) * inverseMeshWorld;
                    worldMatrices[jointIndex] = world;
                    var parentIndex = skeleton.Joints[jointIndex].ParentIndex;
                    var local = world;
                    if (parentIndex >= 0)
                    {
                        if (!Matrix4x4.Invert(worldMatrices[parentIndex], out var inverseParent))
                        {
                            throw new InvalidDataException(
                                "Animated GLB parent transform is not invertible.");
                        }
                        local *= inverseParent;
                    }
                    if (!Matrix4x4.Decompose(local, out var scale, out var rotation,
                        out var translation))
                    {
                        throw new InvalidDataException(
                            "Animated GLB joint transform cannot be decomposed.");
                    }
                    rotation = Quaternion.Normalize(rotation);
                    translations[jointIndex][sample] = translation;
                    rotations[jointIndex][sample] = new Vector4(
                        rotation.X, rotation.Y, rotation.Z, rotation.W);
                    scales[jointIndex][sample] = scale;
                }
            }
            for (var jointIndex = 0; jointIndex < skeleton.Joints.Length; jointIndex++)
            {
                tracks[jointIndex] = new ImportedJointTrack(
                    CreateVectorTrack(times, translations[jointIndex]),
                    CreateQuaternionTrack(times, rotations[jointIndex]),
                    CreateVectorTrack(times, scales[jointIndex]));
            }
            result[animationIndex] = new ImportedAnimation(
                string.IsNullOrWhiteSpace(animation.Name)
                    ? $"animation-{animationIndex}" : animation.Name,
                duration,
                tracks);
        }
        return result;
    }

    /// <summary>Collapses a constant baked vector curve to one key.</summary>
    /// <param name="times">Baked sample times.</param>
    /// <param name="values">Baked vector values.</param>
    /// <returns>Compact imported track.</returns>
    private static ImportedVectorTrack CreateVectorTrack(float[] times, Vector3[] values)
    {
        var constant = true;
        for (var index = 1; index < values.Length; index++)
        {
            if (Vector3.DistanceSquared(values[0], values[index]) <= 1e-12f)
                continue;
            constant = false;
            break;
        }
        return constant
            ? new ImportedVectorTrack([0f], [values[0]], 1)
            : new ImportedVectorTrack(times, values, 1);
    }

    /// <summary>Collapses a constant baked quaternion curve to one key.</summary>
    /// <param name="times">Baked sample times.</param>
    /// <param name="values">Baked XYZW values.</param>
    /// <returns>Compact imported track.</returns>
    private static ImportedQuaternionTrack CreateQuaternionTrack(
        float[] times,
        Vector4[] values)
    {
        var first = new Quaternion(values[0].X, values[0].Y, values[0].Z, values[0].W);
        var constant = true;
        for (var index = 1; index < values.Length; index++)
        {
            var value = new Quaternion(values[index].X, values[index].Y,
                values[index].Z, values[index].W);
            if (MathF.Abs(Quaternion.Dot(first, value)) >= 1f - 1e-6f)
                continue;
            constant = false;
            break;
        }
        return constant
            ? new ImportedQuaternionTrack([0f], [values[0]], 1)
            : new ImportedQuaternionTrack(times, values, 1);
    }

    /// <summary>Imports glTF standard material factors as independently addressable artifacts.</summary>
    /// <param name="context">Artifact output context.</param>
    /// <param name="root">glTF JSON root.</param>
    /// <returns>Published material artifacts.</returns>
    private static IReadOnlyList<AssetArtifact> ImportMaterials(
        AssetImportContext context,
        JsonElement root)
    {
        if (!root.TryGetProperty("materials", out var materials))
            return [];
        var artifacts = new List<AssetArtifact>();
        var materialIndex = 0;
        foreach (var material in materials.EnumerateArray())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var name = material.TryGetProperty("name", out var nameElement)
                ? Sanitize(nameElement.GetString(), $"material-{materialIndex}")
                : $"material-{materialIndex}";
            var pbr = material.TryGetProperty("pbrMetallicRoughness", out var pbrElement)
                ? pbrElement : default;
            var baseColor = Vector4.One;
            if (pbr.ValueKind != JsonValueKind.Undefined &&
                pbr.TryGetProperty("baseColorFactor", out var factor))
            {
                baseColor = new Vector4(factor[0].GetSingle(), factor[1].GetSingle(),
                    factor[2].GetSingle(), factor[3].GetSingle());
            }
            var metallic = pbr.ValueKind != JsonValueKind.Undefined &&
                pbr.TryGetProperty("metallicFactor", out var metallicElement)
                ? metallicElement.GetSingle() : 1f;
            var roughness = pbr.ValueKind != JsonValueKind.Undefined &&
                pbr.TryGetProperty("roughnessFactor", out var roughnessElement)
                ? roughnessElement.GetSingle() : 1f;
            var textureSlot = -1;
            if (pbr.ValueKind != JsonValueKind.Undefined &&
                pbr.TryGetProperty("baseColorTexture", out var texture) &&
                texture.TryGetProperty("index", out var textureIndex))
            {
                textureSlot = textureIndex.GetInt32();
            }
            var doubleSided = material.TryGetProperty("doubleSided", out var doubleSidedElement) &&
                doubleSidedElement.GetBoolean();
            var relativePath = $"materials/{name}-{materialIndex}.nmaterial";
            using (var output = context.CreateArtifact(relativePath))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("NMATL001"u8);
                writer.Write(1u);
                Write(writer, baseColor);
                writer.Write(metallic);
                writer.Write(roughness);
                writer.Write(doubleSided);
                writer.Write(textureSlot);
            }
            artifacts.Add(new AssetArtifact($"material/{materialIndex}",
                "nico/standard-material", relativePath));
            materialIndex++;
        }
        return artifacts;
    }

    /// <summary>Extracts embedded GLB images using texture-index sub-asset identities.</summary>
    /// <param name="context">Artifact output context.</param>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <returns>Published compressed texture artifacts.</returns>
    private static IReadOnlyList<AssetArtifact> ImportTextures(
        AssetImportContext context,
        JsonElement root,
        byte[] binary)
    {
        if (!root.TryGetProperty("textures", out var textures))
            return [];
        if (!root.TryGetProperty("images", out var images))
            throw new InvalidDataException("GLB textures reference a missing images array.");
        var artifacts = new List<AssetArtifact>();
        var textureIndex = 0;
        foreach (var texture in textures.EnumerateArray())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var sourceIndex = texture.GetProperty("source").GetInt32();
            if ((uint)sourceIndex >= images.GetArrayLength())
                throw new InvalidDataException("GLB texture image index is out of range.");
            var image = images[sourceIndex];
            if (!image.TryGetProperty("bufferView", out var bufferViewElement))
                throw new InvalidDataException("GLB external image URIs are not supported.");
            var mimeType = image.GetProperty("mimeType").GetString();
            _ = mimeType switch
            {
                "image/png" => true,
                "image/jpeg" => true,
                _ => throw new InvalidDataException($"GLB image type '{mimeType}' is unsupported.")
            };
            var bytes = ReadBufferView(root, binary, bufferViewElement.GetInt32(), "image");
            ImageResult decoded;
            try
            {
                decoded = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("GLB embedded image could not be decoded.", exception);
            }
            var relativePath = $"textures/texture-{textureIndex}.ntexture";
            using (var output = context.CreateArtifact(relativePath))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("NTEX0001"u8);
                writer.Write(1u);
                writer.Write(checked((uint)decoded.Width));
                writer.Write(checked((uint)decoded.Height));
                writer.Write((byte)1);
                writer.Write(decoded.Data);
            }
            artifacts.Add(new AssetArtifact($"texture/{textureIndex}",
                "nico/texture2d", relativePath));
            textureIndex++;
        }
        return artifacts;
    }

    /// <summary>Copies one validated binary buffer view.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="viewIndex">Buffer-view index.</param>
    /// <param name="purpose">Purpose used in diagnostics.</param>
    /// <returns>The copied buffer-view bytes.</returns>
    private static byte[] ReadBufferView(
        JsonElement root,
        byte[] binary,
        int viewIndex,
        string purpose)
    {
        var views = root.GetProperty("bufferViews");
        if ((uint)viewIndex >= views.GetArrayLength())
            throw new InvalidDataException($"GLB {purpose} buffer view is out of range.");
        var view = views[viewIndex];
        if (view.GetProperty("buffer").GetInt32() != 0)
            throw new InvalidDataException("GLB external buffers are not supported.");
        var offset = view.TryGetProperty("byteOffset", out var offsetElement)
            ? offsetElement.GetInt32() : 0;
        var length = view.GetProperty("byteLength").GetInt32();
        if (offset < 0 || length < 0 || (long)offset + length > binary.Length)
            throw new InvalidDataException($"GLB {purpose} buffer view exceeds the binary chunk.");
        return binary.AsSpan(offset, length).ToArray();
    }

    /// <summary>Builds direct visual-parent indices from node child arrays.</summary>
    /// <param name="nodes">glTF nodes array.</param>
    /// <returns>Parent index per node, or -1 for roots.</returns>
    private static int[] BuildNodeParents(JsonElement nodes)
    {
        var parents = Enumerable.Repeat(-1, nodes.GetArrayLength()).ToArray();
        for (var parentIndex = 0; parentIndex < nodes.GetArrayLength(); parentIndex++)
        {
            var parent = nodes[parentIndex];
            if (!parent.TryGetProperty("children", out var children))
                continue;
            foreach (var childElement in children.EnumerateArray())
            {
                var child = childElement.GetInt32();
                if ((uint)child >= parents.Length || parents[child] >= 0)
                    throw new InvalidDataException("GLB node hierarchy is invalid.");
                parents[child] = parentIndex;
            }
        }
        return parents;
    }

    /// <summary>Computes one node world matrix with cycle detection and caching.</summary>
    /// <param name="nodes">glTF nodes array.</param>
    /// <param name="parents">Parent indices.</param>
    /// <param name="nodeIndex">Node to evaluate.</param>
    /// <param name="cache">World-matrix cache.</param>
    /// <param name="states">Zero/unvisited, one/visiting, or two/complete states.</param>
    /// <returns>Row-vector world matrix.</returns>
    private static Matrix4x4 ComputeNodeWorldMatrix(
        JsonElement nodes,
        int[] parents,
        int nodeIndex,
        Matrix4x4[] cache,
        byte[] states)
    {
        if (states[nodeIndex] == 2)
            return cache[nodeIndex];
        if (states[nodeIndex] == 1)
            throw new InvalidDataException("GLB node hierarchy contains a cycle.");
        states[nodeIndex] = 1;
        var world = ReadNodeLocalMatrix(nodes[nodeIndex]);
        if (parents[nodeIndex] >= 0)
        {
            world *= ComputeNodeWorldMatrix(nodes, parents, parents[nodeIndex], cache, states);
        }
        cache[nodeIndex] = world;
        states[nodeIndex] = 2;
        return world;
    }

    /// <summary>Reads a glTF node transform into the engine's row-vector convention.</summary>
    /// <param name="node">Node JSON.</param>
    /// <returns>Local transform matrix.</returns>
    private static Matrix4x4 ReadNodeLocalMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            if (matrix.GetArrayLength() != 16)
                throw new InvalidDataException("GLB node matrix must contain 16 values.");
            return new Matrix4x4(
                matrix[0].GetSingle(), matrix[1].GetSingle(), matrix[2].GetSingle(),
                matrix[3].GetSingle(), matrix[4].GetSingle(), matrix[5].GetSingle(),
                matrix[6].GetSingle(), matrix[7].GetSingle(), matrix[8].GetSingle(),
                matrix[9].GetSingle(), matrix[10].GetSingle(), matrix[11].GetSingle(),
                matrix[12].GetSingle(), matrix[13].GetSingle(), matrix[14].GetSingle(),
                matrix[15].GetSingle());
        }
        var translation = node.TryGetProperty("translation", out var translationElement)
            ? ReadJsonVector3(translationElement) : Vector3.Zero;
        var scale = node.TryGetProperty("scale", out var scaleElement)
            ? ReadJsonVector3(scaleElement) : Vector3.One;
        var rotation = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out var rotationElement))
        {
            rotation = Quaternion.Normalize(new Quaternion(
                rotationElement[0].GetSingle(), rotationElement[1].GetSingle(),
                rotationElement[2].GetSingle(), rotationElement[3].GetSingle()));
        }
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(translation);
    }

    /// <summary>Reads a three-component JSON vector.</summary>
    /// <param name="element">JSON array.</param>
    /// <returns>Decoded vector.</returns>
    private static Vector3 ReadJsonVector3(JsonElement element)
    {
        if (element.GetArrayLength() != 3)
            throw new InvalidDataException("GLB transform vector must contain three values.");
        return new Vector3(element[0].GetSingle(), element[1].GetSingle(),
            element[2].GetSingle());
    }

    /// <summary>Reads one floating-point MAT4 accessor in row-vector representation.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <returns>Decoded matrices.</returns>
    private static Matrix4x4[] ReadMatrices(
        JsonElement root,
        byte[] binary,
        int accessorIndex)
    {
        var view = ResolveAccessor(root, binary, accessorIndex, "MAT4", 5126,
            "inverse bind matrices");
        var result = new Matrix4x4[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = new Matrix4x4(
                ReadSingle(binary, offset), ReadSingle(binary, offset + 4),
                ReadSingle(binary, offset + 8), ReadSingle(binary, offset + 12),
                ReadSingle(binary, offset + 16), ReadSingle(binary, offset + 20),
                ReadSingle(binary, offset + 24), ReadSingle(binary, offset + 28),
                ReadSingle(binary, offset + 32), ReadSingle(binary, offset + 36),
                ReadSingle(binary, offset + 40), ReadSingle(binary, offset + 44),
                ReadSingle(binary, offset + 48), ReadSingle(binary, offset + 52),
                ReadSingle(binary, offset + 56), ReadSingle(binary, offset + 60));
        }
        return result;
    }

    /// <summary>Reads and remaps an unsigned JOINTS_0 accessor.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="sourceToOrdered">Source-skin to ordered-skeleton mapping.</param>
    /// <returns>Four ordered joint indices per vertex.</returns>
    private static JointIndices[] ReadJointIndices(
        JsonElement root,
        byte[] binary,
        int accessorIndex,
        int[] sourceToOrdered)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var componentType = accessor.GetProperty("componentType").GetInt32();
        if (componentType is not 5121 and not 5123)
            throw new InvalidDataException("GLB JOINTS_0 must use unsigned bytes or shorts.");
        var view = ResolveAccessor(root, binary, accessorIndex, "VEC4", componentType,
            "JOINTS_0");
        var componentSize = componentType == 5121 ? 1 : 2;
        var result = new JointIndices[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = new JointIndices(
                RemapJoint(ReadUnsigned(binary, offset, componentSize), sourceToOrdered),
                RemapJoint(ReadUnsigned(binary, offset + componentSize, componentSize),
                    sourceToOrdered),
                RemapJoint(ReadUnsigned(binary, offset + componentSize * 2, componentSize),
                    sourceToOrdered),
                RemapJoint(ReadUnsigned(binary, offset + componentSize * 3, componentSize),
                    sourceToOrdered));
        }
        return result;
    }

    /// <summary>Reads a WEIGHTS_0 accessor and normalizes every influence set.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <returns>Normalized four-component weights.</returns>
    private static Vector4[] ReadWeights(JsonElement root, byte[] binary, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var componentType = accessor.GetProperty("componentType").GetInt32();
        if (componentType is not 5121 and not 5123 and not 5126)
            throw new InvalidDataException("GLB WEIGHTS_0 component type is unsupported.");
        if (componentType != 5126 &&
            (!accessor.TryGetProperty("normalized", out var normalized) ||
             !normalized.GetBoolean()))
        {
            throw new InvalidDataException("Integer GLB WEIGHTS_0 values must be normalized.");
        }
        var view = ResolveAccessor(root, binary, accessorIndex, "VEC4", componentType,
            "WEIGHTS_0");
        var componentSize = componentType switch { 5121 => 1, 5123 => 2, _ => 4 };
        var denominator = componentType switch { 5121 => 255f, 5123 => 65535f, _ => 1f };
        var result = new Vector4[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            float ReadComponent(int componentOffset) => componentType == 5126
                ? ReadSingle(binary, componentOffset)
                : ReadUnsigned(binary, componentOffset, componentSize) / denominator;
            var value = new Vector4(ReadComponent(offset),
                ReadComponent(offset + componentSize),
                ReadComponent(offset + componentSize * 2),
                ReadComponent(offset + componentSize * 3));
            if (!IsFiniteNonNegative(value))
                throw new InvalidDataException("GLB skin weights must be finite and non-negative.");
            var sum = value.X + value.Y + value.Z + value.W;
            result[index] = sum > float.Epsilon
                ? value / sum : new Vector4(1f, 0f, 0f, 0f);
        }
        return result;
    }

    /// <summary>Reads one unsigned integer component.</summary>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="offset">Byte offset.</param>
    /// <param name="componentSize">One or two bytes.</param>
    /// <returns>Decoded unsigned value.</returns>
    private static uint ReadUnsigned(byte[] binary, int offset, int componentSize) =>
        componentSize == 1 ? binary[offset] : BitConverter.ToUInt16(binary, offset);

    /// <summary>Maps a source skin joint index to ordered skeleton space.</summary>
    /// <param name="sourceIndex">Index stored by JOINTS_0.</param>
    /// <param name="sourceToOrdered">Source-to-ordered mapping.</param>
    /// <returns>Ordered skeleton joint index.</returns>
    private static uint RemapJoint(uint sourceIndex, int[] sourceToOrdered)
    {
        if (sourceIndex >= sourceToOrdered.Length)
            throw new InvalidDataException("GLB vertex references a missing skin joint.");
        return checked((uint)sourceToOrdered[sourceIndex]);
    }

    /// <summary>Checks that skin weights are finite and non-negative.</summary>
    /// <param name="value">Weights to validate.</param>
    /// <returns>True for valid weights.</returns>
    private static bool IsFiniteNonNegative(Vector4 value) =>
        float.IsFinite(value.X) && value.X >= 0f &&
        float.IsFinite(value.Y) && value.Y >= 0f &&
        float.IsFinite(value.Z) && value.Z >= 0f &&
        float.IsFinite(value.W) && value.W >= 0f;

    /// <summary>Reads one tightly or strided floating-point VEC2 accessor.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="semantic">Attribute semantic used in diagnostics.</param>
    /// <returns>Decoded vectors.</returns>
    private static Vector2[] ReadVector2(JsonElement root, byte[] binary, int accessorIndex, string semantic)
    {
        var view = ResolveAccessor(root, binary, accessorIndex, "VEC2", 5126, semantic);
        var result = new Vector2[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = new Vector2(ReadSingle(binary, offset), ReadSingle(binary, offset + 4));
        }
        return result;
    }

    /// <summary>Reads one tightly or strided floating-point VEC3 accessor.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="semantic">Attribute semantic used in diagnostics.</param>
    /// <returns>Decoded vectors.</returns>
    private static Vector3[] ReadVector3(JsonElement root, byte[] binary, int accessorIndex, string semantic)
    {
        var view = ResolveAccessor(root, binary, accessorIndex, "VEC3", 5126, semantic);
        var result = new Vector3[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = new Vector3(ReadSingle(binary, offset), ReadSingle(binary, offset + 4),
                ReadSingle(binary, offset + 8));
        }
        return result;
    }

    /// <summary>Reads one tightly or strided floating-point VEC4 accessor.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="semantic">Attribute semantic used in diagnostics.</param>
    /// <returns>Decoded vectors.</returns>
    private static Vector4[] ReadVector4(JsonElement root, byte[] binary, int accessorIndex, string semantic)
    {
        var view = ResolveAccessor(root, binary, accessorIndex, "VEC4", 5126, semantic);
        var result = new Vector4[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = new Vector4(ReadSingle(binary, offset), ReadSingle(binary, offset + 4),
                ReadSingle(binary, offset + 8), ReadSingle(binary, offset + 12));
        }
        return result;
    }

    /// <summary>Reads normalized RGB or RGBA vertex colors into linear four-component values.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">COLOR_0 accessor index.</param>
    /// <returns>Decoded vertex colors with opaque alpha for RGB sources.</returns>
    private static Vector4[] ReadColors(JsonElement root, byte[] binary, int accessorIndex)
    {
        var accessor = root.GetProperty("accessors")[accessorIndex];
        var type = accessor.GetProperty("type").GetString();
        if (type is not "VEC3" and not "VEC4")
            throw new InvalidDataException("GLB COLOR_0 must use VEC3 or VEC4 values.");
        var componentType = accessor.GetProperty("componentType").GetInt32();
        if (componentType is not 5121 and not 5123 and not 5126)
            throw new InvalidDataException("GLB COLOR_0 component type is unsupported.");
        if (componentType != 5126 &&
            (!accessor.TryGetProperty("normalized", out var normalized) ||
             !normalized.GetBoolean()))
        {
            throw new InvalidDataException("Integer GLB COLOR_0 values must be normalized.");
        }
        var view = ResolveAccessor(root, binary, accessorIndex, type, componentType, "COLOR_0");
        var componentSize = componentType switch { 5121 => 1, 5123 => 2, _ => 4 };
        var denominator = componentType switch { 5121 => 255f, 5123 => 65535f, _ => 1f };
        var result = new Vector4[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            float ReadComponent(int componentOffset) => componentType == 5126
                ? ReadSingle(binary, componentOffset)
                : ReadUnsigned(binary, componentOffset, componentSize) / denominator;
            result[index] = new Vector4(
                ReadComponent(offset),
                ReadComponent(offset + componentSize),
                ReadComponent(offset + componentSize * 2),
                type == "VEC4" ? ReadComponent(offset + componentSize * 3) : 1f);
        }
        return result;
    }

    /// <summary>Reads an unsigned scalar index accessor into a uniform 32-bit representation.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <returns>Decoded indices.</returns>
    private static uint[] ReadIndices(JsonElement root, byte[] binary, int accessorIndex)
    {
        var accessors = root.GetProperty("accessors");
        var accessor = accessors[accessorIndex];
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var elementSize = componentType switch { 5121 => 1, 5123 => 2, 5125 => 4,
            _ => throw new InvalidDataException("GLB indices must be unsigned bytes, shorts, or ints.") };
        var view = ResolveAccessor(root, binary, accessorIndex, "SCALAR", componentType, "indices");
        var result = new uint[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = elementSize switch
            {
                1 => binary[offset],
                2 => BitConverter.ToUInt16(binary, offset),
                _ => BitConverter.ToUInt32(binary, offset)
            };
        }
        return result;
    }

    /// <summary>Resolves and validates an accessor's binary range.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="type">Required accessor shape.</param>
    /// <param name="componentType">Required component type.</param>
    /// <param name="semantic">Attribute semantic used in diagnostics.</param>
    /// <returns>Validated accessor range.</returns>
    private static AccessorView ResolveAccessor(
        JsonElement root, byte[] binary, int accessorIndex, string type, int componentType,
        string semantic)
    {
        var accessors = root.GetProperty("accessors");
        if ((uint)accessorIndex >= accessors.GetArrayLength())
            throw new InvalidDataException($"GLB {semantic} accessor is out of range.");
        var accessor = accessors[accessorIndex];
        if (accessor.GetProperty("type").GetString() != type ||
            accessor.GetProperty("componentType").GetInt32() != componentType ||
            !accessor.TryGetProperty("bufferView", out var bufferViewIndex))
        {
            throw new InvalidDataException($"GLB {semantic} accessor has an unsupported layout.");
        }
        var componentSize = componentType switch { 5121 => 1, 5123 => 2, 5125 or 5126 => 4,
            _ => throw new InvalidDataException($"GLB {semantic} component type is unsupported.") };
        var components = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4,
            "MAT4" => 16,
            _ => throw new InvalidDataException($"GLB {semantic} type is unsupported.") };
        var elementSize = componentSize * components;
        var bufferViews = root.GetProperty("bufferViews");
        var view = bufferViews[bufferViewIndex.GetInt32()];
        if (view.GetProperty("buffer").GetInt32() != 0)
            throw new InvalidDataException("GLB external buffers are not supported.");
        var offset = (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0)
            + (accessor.TryGetProperty("byteOffset", out var accessorOffset)
                ? accessorOffset.GetInt32() : 0);
        var stride = view.TryGetProperty("byteStride", out var strideElement)
            ? strideElement.GetInt32() : elementSize;
        var count = accessor.GetProperty("count").GetInt32();
        if (offset < 0 || count < 0 || stride < elementSize ||
            (count > 0 && (long)offset + (long)(count - 1) * stride + elementSize > binary.Length))
        {
            throw new InvalidDataException($"GLB {semantic} accessor exceeds the binary chunk.");
        }
        return new AccessorView(offset, count, stride);
    }

    /// <summary>Generates smooth vertex normals from indexed triangles.</summary>
    /// <param name="positions">Object-space positions.</param>
    /// <param name="indices">Triangle-list indices.</param>
    /// <param name="normals">Destination normal array.</param>
    private static void GenerateNormals(Vector3[] positions, uint[] indices, Vector3[] normals)
    {
        for (var index = 0; index < indices.Length; index += 3)
        {
            var first = checked((int)indices[index]);
            var second = checked((int)indices[index + 1]);
            var third = checked((int)indices[index + 2]);
            var face = Vector3.Cross(positions[second] - positions[first],
                positions[third] - positions[first]);
            normals[first] += face;
            normals[second] += face;
            normals[third] += face;
        }
        for (var index = 0; index < normals.Length; index++)
            normals[index] = normals[index].LengthSquared() > 0f
                ? Vector3.Normalize(normals[index]) : Vector3.UnitY;
    }

    /// <summary>Writes one versioned little-endian Nico static-mesh artifact.</summary>
    /// <param name="output">Artifact output stream.</param>
    /// <param name="positions">Object-space positions.</param>
    /// <param name="normals">Object-space normals.</param>
    /// <param name="texCoords">Primary texture coordinates.</param>
    /// <param name="tangents">Tangent vectors and handedness.</param>
    /// <param name="colors">Linear per-vertex colors.</param>
    /// <param name="indices">Triangle-list indices.</param>
    /// <param name="materialSlot">Source material slot.</param>
    private static void WriteMesh(
        Stream output, Vector3[] positions, Vector3[] normals, Vector2[] texCoords,
        Vector4[] tangents, Vector4[] colors, uint[] indices, int materialSlot)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("NMESH001"u8);
        writer.Write(2u);
        writer.Write(checked((uint)positions.Length));
        writer.Write(checked((uint)indices.Length));
        writer.Write(materialSlot);
        for (var index = 0; index < positions.Length; index++)
        {
            Write(writer, positions[index]);
            Write(writer, normals[index]);
            Write(writer, texCoords[index]);
            Write(writer, tangents[index]);
            Write(writer, colors[index]);
        }
        foreach (var value in indices)
            writer.Write(value);
    }

    /// <summary>Writes a versioned skinned-mesh artifact.</summary>
    /// <param name="output">Artifact output stream.</param>
    /// <param name="positions">Bind-pose positions.</param>
    /// <param name="normals">Bind-pose normals.</param>
    /// <param name="texCoords">Primary texture coordinates.</param>
    /// <param name="tangents">Tangent vectors.</param>
    /// <param name="colors">Linear per-vertex colors.</param>
    /// <param name="joints">Four joint indices per vertex.</param>
    /// <param name="weights">Four normalized weights per vertex.</param>
    /// <param name="indices">Triangle-list indices.</param>
    /// <param name="materialSlot">Source material slot.</param>
    /// <param name="skeleton">Imported skeleton.</param>
    /// <param name="animations">Imported clips.</param>
    private static void WriteSkinnedMesh(
        Stream output,
        Vector3[] positions,
        Vector3[] normals,
        Vector2[] texCoords,
        Vector4[] tangents,
        Vector4[] colors,
        JointIndices[] joints,
        Vector4[] weights,
        uint[] indices,
        int materialSlot,
        ImportedSkeleton skeleton,
        ImportedAnimation[] animations)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("NSKIN001"u8);
        writer.Write(3u);
        writer.Write(checked((uint)positions.Length));
        writer.Write(checked((uint)indices.Length));
        writer.Write(materialSlot);
        Write(writer, skeleton.MeshNodeTransform);
        for (var index = 0; index < positions.Length; index++)
        {
            Write(writer, positions[index]);
            Write(writer, normals[index]);
            Write(writer, texCoords[index]);
            Write(writer, tangents[index]);
            Write(writer, colors[index]);
            writer.Write(joints[index].X);
            writer.Write(joints[index].Y);
            writer.Write(joints[index].Z);
            writer.Write(joints[index].W);
            Write(writer, weights[index]);
        }
        for (var index = 0; index < indices.Length; index++)
            writer.Write(indices[index]);
        writer.Write(checked((uint)skeleton.Joints.Length));
        for (var index = 0; index < skeleton.Joints.Length; index++)
        {
            var joint = skeleton.Joints[index];
            writer.Write(joint.Name);
            writer.Write(joint.ParentIndex);
            Write(writer, joint.Translation);
            Write(writer, new Vector4(joint.Rotation.X, joint.Rotation.Y,
                joint.Rotation.Z, joint.Rotation.W));
            Write(writer, joint.Scale);
            Write(writer, joint.InverseBindMatrix);
        }
        writer.Write(checked((uint)animations.Length));
        for (var index = 0; index < animations.Length; index++)
            WriteAnimation(writer, animations[index], skeleton.Joints.Length);
    }

    /// <summary>Writes one standalone skeletal-animation artifact.</summary>
    /// <param name="output">Artifact output stream.</param>
    /// <param name="skeleton">Source skeleton.</param>
    /// <param name="animation">Single independently addressable clip.</param>
    private static void WriteSkeletalAnimation(
        Stream output,
        ImportedSkeleton skeleton,
        ImportedAnimation animation)
    {
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("NANIM001"u8);
            writer.Write(1u);
            writer.Flush();
        }
        WriteSkinnedMesh(output, [], [], [], [], [], [], [], [], -1,
            skeleton with { MeshNodeTransform = Matrix4x4.Identity }, [animation]);
    }

    /// <summary>Writes one imported animation clip.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="animation">Animation to write.</param>
    /// <param name="jointCount">Expected joint-track count.</param>
    private static void WriteAnimation(
        BinaryWriter writer,
        ImportedAnimation animation,
        int jointCount)
    {
        if (animation.Tracks.Length != jointCount)
            throw new InvalidDataException("Imported animation does not match its skeleton.");
        writer.Write(animation.Name);
        writer.Write(animation.Duration);
        for (var index = 0; index < animation.Tracks.Length; index++)
        {
            var track = animation.Tracks[index];
            writer.Write(track is not null);
            if (track is null)
                continue;
            WriteTrack(writer, track.Translation);
            WriteTrack(writer, track.Rotation);
            WriteTrack(writer, track.Scale);
        }
    }

    /// <summary>Writes one optional vector animation track.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="track">Optional vector track.</param>
    private static void WriteTrack(BinaryWriter writer, ImportedVectorTrack? track)
    {
        writer.Write(track is not null);
        if (track is null)
            return;
        if (track.Times.Length != track.Values.Length)
            throw new InvalidDataException("GLB animation key counts do not match.");
        writer.Write(track.Interpolation);
        writer.Write(checked((uint)track.Times.Length));
        for (var index = 0; index < track.Times.Length; index++)
        {
            writer.Write(track.Times[index]);
            Write(writer, track.Values[index]);
        }
    }

    /// <summary>Writes one optional quaternion animation track.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="track">Optional quaternion track.</param>
    private static void WriteTrack(BinaryWriter writer, ImportedQuaternionTrack? track)
    {
        writer.Write(track is not null);
        if (track is null)
            return;
        if (track.Times.Length != track.Values.Length)
            throw new InvalidDataException("GLB animation key counts do not match.");
        writer.Write(track.Interpolation);
        writer.Write(checked((uint)track.Times.Length));
        for (var index = 0; index < track.Times.Length; index++)
        {
            writer.Write(track.Times[index]);
            var value = track.Values[index];
            var rotation = Quaternion.Normalize(new Quaternion(value.X, value.Y, value.Z, value.W));
            Write(writer, new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W));
        }
    }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector2 value) { writer.Write(value.X); writer.Write(value.Y); }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector4 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W); }

    /// <summary>Writes a row-major matrix.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Matrix value.</param>
    private static void Write(BinaryWriter writer, Matrix4x4 value)
    {
        writer.Write(value.M11); writer.Write(value.M12); writer.Write(value.M13); writer.Write(value.M14);
        writer.Write(value.M21); writer.Write(value.M22); writer.Write(value.M23); writer.Write(value.M24);
        writer.Write(value.M31); writer.Write(value.M32); writer.Write(value.M33); writer.Write(value.M34);
        writer.Write(value.M41); writer.Write(value.M42); writer.Write(value.M43); writer.Write(value.M44);
    }

    /// <summary>Reads one little-endian single.</summary>
    /// <param name="bytes">Source bytes.</param>
    /// <param name="offset">Byte offset.</param>
    /// <returns>Decoded value.</returns>
    private static float ReadSingle(byte[] bytes, int offset)
    {
        return BitConverter.Int32BitsToSingle(BitConverter.ToInt32(bytes, offset));
    }

    /// <summary>Creates a filesystem-safe stable name.</summary>
    /// <param name="value">Untrusted source name.</param>
    /// <param name="fallback">Fallback when the name is empty.</param>
    /// <returns>Filesystem-safe name.</returns>
    private static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var result = new string(value.Select(character => char.IsLetterOrDigit(character) ||
            character is '-' or '_' ? character : '-').ToArray()).Trim('-');
        return result.Length == 0 ? fallback : result;
    }

    /// <summary>Resolved binary accessor range.</summary>
    private readonly record struct AccessorView(int Offset, int Count, int Stride);

    /// <summary>Four remapped joint indices.</summary>
    private readonly record struct JointIndices(uint X, uint Y, uint Z, uint W);

    /// <summary>Associates a skin declaration with the node instantiating its mesh.</summary>
    private readonly record struct SkinBinding(JsonElement Skin, int MeshNodeIndex);

    /// <summary>Imported skeleton joint.</summary>
    private readonly record struct ImportedJoint(
        string Name,
        int ParentIndex,
        Vector3 Translation,
        Quaternion Rotation,
        Vector3 Scale,
        Matrix4x4 InverseBindMatrix);

    /// <summary>Imported skeleton plus source mappings.</summary>
    private readonly record struct ImportedSkeleton(
        ImportedJoint[] Joints,
        int[] SourceNodeIndices,
        int[] SourceJointToSkeletonJoint,
        int MeshNodeIndex,
        Matrix4x4 MeshNodeTransform);

    /// <summary>Imported vector animation curve.</summary>
    private sealed record ImportedVectorTrack(
        float[] Times,
        Vector3[] Values,
        byte Interpolation);

    /// <summary>Imported quaternion animation curve stored in glTF XYZW order.</summary>
    private sealed record ImportedQuaternionTrack(
        float[] Times,
        Vector4[] Values,
        byte Interpolation);

    /// <summary>Optional transform curves for one joint.</summary>
    private sealed record ImportedJointTrack(
        ImportedVectorTrack? Translation,
        ImportedQuaternionTrack? Rotation,
        ImportedVectorTrack? Scale);

    /// <summary>Imported animation clip.</summary>
    private sealed record ImportedAnimation(
        string Name,
        float Duration,
        ImportedJointTrack?[] Tracks);
}
