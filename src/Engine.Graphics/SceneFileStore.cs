using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.Core;
using Engine.Graphics;

namespace Engine.Graphics;

/// <summary>
/// Loads and saves the editor's versioned JSON scene format.
/// </summary>
public static class SceneFileStore
{
    private const int CurrentFormatVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Saves a scene graph and its active game camera atomically.
    /// </summary>
    /// <param name="path">Destination scene-file path.</param>
    /// <param name="root">Synthetic scene root whose children are persisted.</param>
    /// <param name="gameCamera">Camera used by the Game viewport.</param>
    public static void Save(string path, Node3D root, PerspectiveCamera gameCamera)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(gameCamera);

        var context = new SerializationContext(gameCamera);
        var nodes = root.Children.Select(child => EncodeNode(child, context)).ToList();
        if (context.GameCameraId.Length == 0)
            throw new InvalidOperationException("The active game camera must belong to the saved scene graph.");

        var document = new SceneDocument(CurrentFormatVersion, context.GameCameraId, nodes);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The scene path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Loads and validates a scene graph from disk.
    /// </summary>
    /// <param name="path">Source scene-file path.</param>
    /// <returns>The reconstructed scene graph, renderable meshes, and active game camera.</returns>
    public static LoadedScene Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = JsonSerializer.Deserialize<SceneDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The scene file is empty.");
        if (document.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException(
                $"Unsupported scene format version {document.FormatVersion}; expected {CurrentFormatVersion}.");

        if (string.IsNullOrWhiteSpace(document.GameCameraId))
            throw new InvalidDataException("The scene does not identify an active game camera.");
        if (document.Nodes is null)
            throw new InvalidDataException("The scene does not contain a node collection.");

        var root = new Node3D { Name = "Scene" };
        var nodesById = new Dictionary<string, Node>(StringComparer.Ordinal);
        var meshInstances = new List<MeshInstance3D>();
        foreach (var nodeData in document.Nodes)
            root.AddChild(DecodeNode(nodeData, nodesById, meshInstances));

        if (!nodesById.TryGetValue(document.GameCameraId, out var cameraNode)
            || cameraNode is not PerspectiveCamera gameCamera)
            throw new InvalidDataException("The scene's gameCameraId does not identify a perspective camera.");

        return new LoadedScene(root, meshInstances, gameCamera);
    }

    /// <summary>
    /// Encodes one scene node and its descendants.
    /// </summary>
    /// <param name="node">Node to encode.</param>
    /// <param name="context">Serialization state containing the active camera reference.</param>
    /// <returns>Serializable node data.</returns>
    private static SceneNodeData EncodeNode(Node node, SerializationContext context)
    {
        var id = Guid.NewGuid().ToString("N");
        if (ReferenceEquals(node, context.GameCamera))
            context.GameCameraId = id;

        var type = node switch
        {
            PerspectiveCamera => SceneNodeType.PerspectiveCamera,
            MeshInstance3D => SceneNodeType.AssetMesh,
            Node3D when node.GetType() == typeof(Node3D) => SceneNodeType.Node3D,
            _ => throw new NotSupportedException($"Scene node type '{node.GetType().Name}' cannot be saved.")
        };
        var camera = node as PerspectiveCamera;
        var children = node.Children.Select(child => EncodeNode(child, context)).ToList();
        return new SceneNodeData(
            id,
            type,
            node.Name,
            SceneVector3.From(node.Position),
            SceneVector3.From(node.Rotation),
            SceneVector3.From(node.Scale),
            node.ScriptId,
            camera is null ? null : new CameraData(camera.Fov, camera.Near, camera.Far),
            node is MeshInstance3D meshInstance
                ? new ModelData(meshInstance.Mesh.Asset, meshInstance.Mesh.SubAsset,
                    meshInstance.Materials.ToList()) : null,
            node is MeshInstance3D { MaterialOverride: { } materialOverride }
                ? MaterialOverrideData.From(materialOverride) : null,
            children);
    }

    /// <summary>
    /// Reconstructs one scene node and its descendants.
    /// </summary>
    /// <param name="data">Serialized node data.</param>
    /// <param name="nodesById">Index used to resolve scene references.</param>
    /// <param name="meshInstances">Collection receiving renderable mesh nodes.</param>
    /// <returns>The reconstructed scene node.</returns>
    private static Node DecodeNode(
        SceneNodeData data,
        IDictionary<string, Node> nodesById,
        ICollection<MeshInstance3D> meshInstances)
    {
        if (string.IsNullOrWhiteSpace(data.Id) || nodesById.ContainsKey(data.Id))
            throw new InvalidDataException($"Scene node ID '{data.Id}' is empty or duplicated.");

        Node3D node = data.Type switch
        {
            SceneNodeType.Node3D => new Node3D(),
            SceneNodeType.Cube => new MeshInstance3D(),
            SceneNodeType.ImportedModel or SceneNodeType.AssetMesh => CreateAssetMesh(data.Model),
            SceneNodeType.PerspectiveCamera => CreateCamera(data.Camera),
            _ => throw new InvalidDataException($"Unsupported scene node type '{data.Type}'.")
        };
        node.Name = data.Name ?? string.Empty;
        node.Position = data.Position.ToVector3();
        node.Rotation = data.Rotation.ToVector3();
        node.Scale = data.Scale.ToVector3();
        node.ScriptId = data.ScriptId;
        if (node is MeshInstance3D meshNode && data.MaterialOverride is not null)
            meshNode.MaterialOverride = data.MaterialOverride.ToMaterial();
        nodesById.Add(data.Id, node);
        if (node is MeshInstance3D meshInstance)
            meshInstances.Add(meshInstance);
        if (data.Children is null)
            throw new InvalidDataException($"Scene node '{data.Id}' does not contain a child collection.");
        foreach (var child in data.Children)
            node.AddChild(DecodeNode(child, nodesById, meshInstances));
        return node;
    }

    /// <summary>
    /// Creates a perspective camera from serialized camera settings.
    /// </summary>
    /// <param name="data">Serialized camera settings.</param>
    /// <returns>A perspective camera.</returns>
    private static PerspectiveCamera CreateCamera(CameraData? data)
    {
        if (data is null)
            throw new InvalidDataException("A perspective camera node is missing camera settings.");
        return new PerspectiveCamera(data.Fov, near: data.Near, far: data.Far);
    }

    /// <summary>Creates a mesh node from its persistent resource references.</summary>
    /// <param name="model">Serialized imported mesh reference.</param>
    /// <returns>The reconstructed mesh node.</returns>
    private static MeshInstance3D CreateAssetMesh(ModelData? model)
    {
        if (model is null || model.Asset.Value == Guid.Empty)
            throw new InvalidDataException("An asset mesh node is missing its mesh reference.");
        var instance = new MeshInstance3D
        {
            Mesh = new AssetReference(model.Asset, model.SubAsset)
        };
        if (model.Materials is not null)
            instance.Materials.AddRange(model.Materials);
        return instance;
    }

    private sealed record SceneDocument(int FormatVersion, string GameCameraId, List<SceneNodeData> Nodes);

    private sealed record SceneNodeData(
        string Id,
        SceneNodeType Type,
        string? Name,
        SceneVector3 Position,
        SceneVector3 Rotation,
        SceneVector3 Scale,
        AssetId? ScriptId,
        CameraData? Camera,
        ModelData? Model,
        MaterialOverrideData? MaterialOverride,
        List<SceneNodeData> Children);

    private sealed record CameraData(float Fov, float Near, float Far);

    private sealed record ModelData(
        AssetId Asset,
        string? SubAsset,
        List<AssetReference>? Materials = null);

    private sealed record MaterialOverrideData(
        SceneVector4 BaseColor,
        float Metallic,
        float Roughness,
        bool DoubleSided,
        AssetReference? BaseColorTexture)
    {
        /// <summary>Encodes editable material values.</summary>
        /// <param name="material">Scene-local material.</param>
        /// <returns>Serializable material data.</returns>
        public static MaterialOverrideData From(MaterialProperties material) => new(
            SceneVector4.From(material.BaseColor), material.Metallic, material.Roughness,
            material.DoubleSided, material.BaseColorTexture);

        /// <summary>Decodes editable material values.</summary>
        /// <returns>A scene-local material.</returns>
        public MaterialProperties ToMaterial() => new()
        {
            BaseColor = BaseColor.ToVector4(),
            Metallic = Metallic,
            Roughness = Roughness,
            DoubleSided = DoubleSided,
            BaseColorTexture = BaseColorTexture
        };
    }

    private sealed class SerializationContext
    {
        /// <summary>Gets the active camera reference.</summary>
        public PerspectiveCamera GameCamera { get; }

        /// <summary>Gets or sets the serialized identifier assigned to the active camera.</summary>
        public string GameCameraId { get; set; } = string.Empty;

        /// <summary>Creates serialization state for one scene.</summary>
        /// <param name="gameCamera">Active camera reference.</param>
        public SerializationContext(PerspectiveCamera gameCamera)
        {
            GameCamera = gameCamera;
        }
    }

    private readonly record struct SceneVector3(float X, float Y, float Z)
    {
        /// <summary>Creates serializable vector data.</summary>
        /// <param name="value">Runtime vector.</param>
        /// <returns>Serializable vector.</returns>
        public static SceneVector3 From(Vector3 value) => new(value.X, value.Y, value.Z);

        /// <summary>Creates a runtime vector.</summary>
        /// <returns>Runtime vector.</returns>
        public Vector3 ToVector3() => new(X, Y, Z);
    }

    private readonly record struct SceneVector4(float X, float Y, float Z, float W)
    {
        /// <summary>Creates serializable vector data.</summary>
        /// <param name="value">Runtime vector.</param>
        /// <returns>Serializable vector data.</returns>
        public static SceneVector4 From(Vector4 value) => new(value.X, value.Y, value.Z, value.W);

        /// <summary>Creates a runtime vector.</summary>
        /// <returns>Runtime vector.</returns>
        public Vector4 ToVector4() => new(X, Y, Z, W);
    }

    private enum SceneNodeType
    {
        Node3D,
        Cube,
        ImportedModel,
        AssetMesh,
        PerspectiveCamera
    }
}

/// <summary>
/// Contains a scene reconstructed from a scene file.
/// </summary>
/// <param name="Root">Synthetic scene root.</param>
/// <param name="MeshInstances">Renderable mesh instances in the scene.</param>
/// <param name="GameCamera">Camera selected for the Game viewport.</param>
public sealed record LoadedScene(
    Node3D Root,
    List<MeshInstance3D> MeshInstances,
    PerspectiveCamera GameCamera);
