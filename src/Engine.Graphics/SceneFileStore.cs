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
    private const int CurrentFormatVersion = 7;
    private const int MinimumFormatVersion = 3;
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
        if (document.FormatVersion < MinimumFormatVersion ||
            document.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $"Unsupported scene format version {document.FormatVersion}; expected " +
                $"{MinimumFormatVersion} through {CurrentFormatVersion}.");

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
            null,
            EncodeComponents(node),
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
        DecodeComponents(data, node);
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

    /// <summary>Encodes all supported components attached to one node.</summary>
    /// <param name="node">Component owner.</param>
    /// <returns>Persistent component records in authored order.</returns>
    private static List<SceneComponentData> EncodeComponents(Node node)
    {
        var result = new List<SceneComponentData>(node.Components.Count);
        var components = node.Components;
        for (var index = 0; index < components.Count; index++)
        {
            switch (components[index])
            {
                case ScriptComponent script:
                    var properties = new List<PropertyOverrideData>(
                        script.PropertyOverrides.Count);
                    var overrides = script.PropertyOverrides;
                    for (var propertyIndex = 0; propertyIndex < overrides.Count; propertyIndex++)
                        properties.Add(PropertyOverrideData.From(overrides[propertyIndex]));
                    result.Add(new SceneComponentData(
                        SceneComponentType.Script, script.Enabled, script.ScriptId, properties));
                    break;
                case RigidBodyComponent rigidBody:
                    result.Add(new SceneComponentData(
                        SceneComponentType.RigidBody,
                        rigidBody.Enabled,
                        RigidBody: new RigidBodyData(
                            rigidBody.MotionType,
                            rigidBody.Mass,
                            SceneVector3.From(rigidBody.LinearVelocity),
                            rigidBody.UseGravity,
                            rigidBody.GravityScale,
                            rigidBody.LinearDamping)));
                    break;
                case ColliderComponent collider:
                    result.Add(new SceneComponentData(
                        SceneComponentType.Collider,
                        collider.Enabled,
                        Collider: new ColliderData(
                            collider.Shape,
                            SceneVector3.From(collider.Center),
                            SceneVector3.From(collider.Size),
                            collider.Radius,
                            collider.Height,
                            collider.IsTrigger,
                            collider.Friction,
                            collider.Restitution,
                            collider.Mesh)));
                    break;
                case AnimatorComponent animator:
                    result.Add(new SceneComponentData(
                        SceneComponentType.Animator,
                        animator.Enabled,
                        Animator: new AnimatorData(
                            animator.AnimationSource,
                            animator.Clip,
                            animator.PlayAutomatically,
                            animator.Loop,
                            animator.Speed)));
                    break;
                default:
                    throw new NotSupportedException(
                        $"Component type '{components[index].GetType().Name}' cannot be saved.");
            }
        }
        return result;
    }

    /// <summary>Restores current component records or the legacy single-script field.</summary>
    /// <param name="data">Serialized node.</param>
    /// <param name="node">Reconstructed component owner.</param>
    private static void DecodeComponents(SceneNodeData data, Node node)
    {
        if (data.Components is null)
        {
            node.ScriptId = data.ScriptId;
            return;
        }
        for (var index = 0; index < data.Components.Count; index++)
        {
            var componentData = data.Components[index];
            Component component;
            switch (componentData.Type)
            {
                case SceneComponentType.Script:
                    if (componentData.ScriptId is not { } scriptId || scriptId.Value == Guid.Empty)
                        throw new InvalidDataException(
                            $"Scene component {index} is not a valid script component.");
                    var script = new ScriptComponent(scriptId);
                    if (componentData.Properties is not null)
                    {
                        for (var propertyIndex = 0;
                             propertyIndex < componentData.Properties.Count;
                             propertyIndex++)
                        {
                            var property = componentData.Properties[propertyIndex];
                            script.SetPropertyOverride(property.PropertyId, property.ToValue());
                        }
                    }
                    component = script;
                    break;
                case SceneComponentType.RigidBody when componentData.RigidBody is { } body:
                    component = new RigidBodyComponent
                    {
                        MotionType = body.MotionType,
                        Mass = body.Mass,
                        LinearVelocity = body.LinearVelocity.ToVector3(),
                        UseGravity = body.UseGravity,
                        GravityScale = body.GravityScale,
                        LinearDamping = body.LinearDamping
                    };
                    break;
                case SceneComponentType.Collider when componentData.Collider is { } collider:
                    component = new ColliderComponent
                    {
                        Shape = collider.Shape,
                        Center = collider.Center.ToVector3(),
                        Size = collider.Size.ToVector3(),
                        Radius = collider.Radius,
                        Height = collider.Height,
                        IsTrigger = collider.IsTrigger,
                        Friction = collider.Friction,
                        Restitution = collider.Restitution,
                        Mesh = collider.Mesh
                    };
                    break;
                case SceneComponentType.Animator when componentData.Animator is { } animator:
                    component = new AnimatorComponent
                    {
                        AnimationSource = animator.AnimationSource,
                        Clip = animator.Clip,
                        PlayAutomatically = animator.PlayAutomatically,
                        Loop = animator.Loop,
                        Speed = animator.Speed
                    };
                    break;
                default:
                    throw new InvalidDataException(
                        $"Scene component {index} has incomplete or unsupported data.");
            }
            component.Enabled = componentData.Enabled;
            node.AddComponent(component);
        }
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
        List<SceneComponentData>? Components,
        CameraData? Camera,
        ModelData? Model,
        MaterialOverrideData? MaterialOverride,
        List<SceneNodeData> Children);

    private sealed record SceneComponentData(
        SceneComponentType Type,
        bool Enabled,
        AssetId? ScriptId = null,
        List<PropertyOverrideData>? Properties = null,
        RigidBodyData? RigidBody = null,
        ColliderData? Collider = null,
        AnimatorData? Animator = null);

    private sealed record RigidBodyData(
        RigidBodyMotionType MotionType,
        float Mass,
        SceneVector3 LinearVelocity,
        bool UseGravity,
        float GravityScale,
        float LinearDamping);

    private sealed record ColliderData(
        ColliderShape Shape,
        SceneVector3 Center,
        SceneVector3 Size,
        float Radius,
        float Height,
        bool IsTrigger,
        float Friction,
        float Restitution,
        AssetReference? Mesh = null);

    private sealed record AnimatorData(
        AssetReference? AnimationSource,
        string? Clip,
        bool PlayAutomatically,
        bool Loop,
        float Speed);

    private sealed record PropertyOverrideData(
        int PropertyId,
        SerializedPropertyValueKind Kind,
        bool? Boolean = null,
        long? SignedInteger = null,
        ulong? UnsignedInteger = null,
        double? Number = null,
        string? Text = null,
        SceneVector4? Vector = null)
    {
        /// <summary>Encodes one persistent script property override.</summary>
        /// <param name="propertyOverride">Authored override.</param>
        /// <returns>Serializable property data.</returns>
        public static PropertyOverrideData From(ScriptPropertyOverride propertyOverride)
        {
            var value = propertyOverride.Value;
            return value.Kind switch
            {
                SerializedPropertyValueKind.Boolean when value.TryGetBoolean(out var boolean) =>
                    new(propertyOverride.PropertyId, value.Kind, Boolean: boolean),
                SerializedPropertyValueKind.SignedInteger
                    when value.TryGetSignedInteger(out var signed) =>
                    new(propertyOverride.PropertyId, value.Kind, SignedInteger: signed),
                SerializedPropertyValueKind.UnsignedInteger
                    when value.TryGetUnsignedInteger(out var unsigned) =>
                    new(propertyOverride.PropertyId, value.Kind, UnsignedInteger: unsigned),
                SerializedPropertyValueKind.Number when value.TryGetNumber(out var number) =>
                    new(propertyOverride.PropertyId, value.Kind, Number: number),
                SerializedPropertyValueKind.String when value.TryGetString(out var text) =>
                    new(propertyOverride.PropertyId, value.Kind, Text: text),
                SerializedPropertyValueKind.Vector2 when value.TryGetVector2(out var vector2) =>
                    new(propertyOverride.PropertyId, value.Kind,
                        Vector: SceneVector4.From(new Vector4(vector2, 0f, 0f))),
                SerializedPropertyValueKind.Vector3 when value.TryGetVector3(out var vector3) =>
                    new(propertyOverride.PropertyId, value.Kind,
                        Vector: SceneVector4.From(new Vector4(vector3, 0f))),
                SerializedPropertyValueKind.Vector4 when value.TryGetVector4(out var vector4) =>
                    new(propertyOverride.PropertyId, value.Kind,
                        Vector: SceneVector4.From(vector4)),
                _ => throw new InvalidDataException(
                    $"Property {propertyOverride.PropertyId} has no serializable value.")
            };
        }

        /// <summary>Decodes one persistent script property override.</summary>
        /// <returns>Validated persistent value.</returns>
        public SerializedPropertyValue ToValue()
        {
            return Kind switch
            {
                SerializedPropertyValueKind.Boolean when Boolean is { } boolean =>
                    SerializedPropertyValue.From(boolean),
                SerializedPropertyValueKind.SignedInteger when SignedInteger is { } signed =>
                    SerializedPropertyValue.From(signed),
                SerializedPropertyValueKind.UnsignedInteger when UnsignedInteger is { } unsigned =>
                    SerializedPropertyValue.From(unsigned),
                SerializedPropertyValueKind.Number when Number is { } number =>
                    SerializedPropertyValue.From(number),
                SerializedPropertyValueKind.String => SerializedPropertyValue.From(Text),
                SerializedPropertyValueKind.Vector2 when Vector is { } vector =>
                    SerializedPropertyValue.From(new Vector2(vector.X, vector.Y)),
                SerializedPropertyValueKind.Vector3 when Vector is { } vector =>
                    SerializedPropertyValue.From(new Vector3(vector.X, vector.Y, vector.Z)),
                SerializedPropertyValueKind.Vector4 when Vector is { } vector =>
                    SerializedPropertyValue.From(vector.ToVector4()),
                _ => throw new InvalidDataException(
                    $"Property {PropertyId} is missing its {Kind} value.")
            };
        }
    }

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

    private enum SceneComponentType
    {
        Script,
        RigidBody,
        Collider,
        Animator
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
