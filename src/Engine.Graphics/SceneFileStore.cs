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
    private const int CurrentFormatVersion = 10;
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
    /// <param name="nodeFactory">Optional higher-level custom node factory.</param>
    /// <returns>The reconstructed scene graph, renderable meshes, and active game camera.</returns>
    public static LoadedScene Load(string path, ISceneNodeFactory? nodeFactory = null)
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
            root.AddChild(DecodeNode(nodeData, nodesById, meshInstances, nodeFactory));

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
            ICustomSceneNode => SceneNodeType.Custom,
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
            (node as ICustomSceneNode)?.SceneTypeId,
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
    /// <param name="nodeFactory">Optional higher-level custom node factory.</param>
    /// <returns>The reconstructed scene node.</returns>
    private static Node DecodeNode(
        SceneNodeData data,
        IDictionary<string, Node> nodesById,
        ICollection<MeshInstance3D> meshInstances,
        ISceneNodeFactory? nodeFactory)
    {
        if (string.IsNullOrWhiteSpace(data.Id) || nodesById.ContainsKey(data.Id))
            throw new InvalidDataException($"Scene node ID '{data.Id}' is empty or duplicated.");

        Node node = data.Type switch
        {
            SceneNodeType.Node3D => new Node3D(),
            SceneNodeType.Cube => new MeshInstance3D(),
            SceneNodeType.ImportedModel or SceneNodeType.AssetMesh => CreateAssetMesh(data.Model),
            SceneNodeType.PerspectiveCamera => CreateCamera(data.Camera),
            SceneNodeType.Custom => CreateCustomNode(data.CustomType, nodeFactory),
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
            node.AddChild(DecodeNode(child, nodesById, meshInstances, nodeFactory));
        return node;
    }

    /// <summary>Creates one higher-level custom node through the supplied factory.</summary>
    /// <param name="sceneTypeId">Stable custom type identifier.</param>
    /// <param name="nodeFactory">Optional higher-level node factory.</param>
    /// <returns>The created detached node.</returns>
    private static Node CreateCustomNode(string? sceneTypeId, ISceneNodeFactory? nodeFactory)
    {
        if (string.IsNullOrWhiteSpace(sceneTypeId))
            throw new InvalidDataException("A custom scene node is missing its type identifier.");
        if (nodeFactory?.TryCreate(sceneTypeId, out var node) != true || node is null)
            throw new InvalidDataException($"Custom scene node type '{sceneTypeId}' is not registered.");
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
                    result.Add(EncodeCollider(collider));
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
                            animator.Speed,
                            animator.DefaultFadeDuration,
                            animator.DefaultClip,
                            animator.AnimationSet)));
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
                case SceneComponentType.Collider when componentData.Collider is { } legacy:
                    component = DecodeLegacyCollider(legacy);
                    break;
                case SceneComponentType.BoxCollider when componentData.Collider is { } box:
                    component = ApplyColliderProperties(new BoxColliderComponent
                    {
                        Size = box.Size.ToVector3()
                    }, box);
                    break;
                case SceneComponentType.SphereCollider when componentData.Collider is { } sphere:
                    component = ApplyColliderProperties(new SphereColliderComponent
                    {
                        Radius = sphere.Radius
                    }, sphere);
                    break;
                case SceneComponentType.CapsuleCollider when componentData.Collider is { } capsule:
                    component = ApplyColliderProperties(new CapsuleColliderComponent
                    {
                        Radius = capsule.Radius,
                        Height = capsule.Height
                    }, capsule);
                    break;
                case SceneComponentType.CylinderCollider when componentData.Collider is { } cylinder:
                    component = ApplyColliderProperties(new CylinderColliderComponent
                    {
                        Radius = cylinder.Radius,
                        Height = cylinder.Height
                    }, cylinder);
                    break;
                case SceneComponentType.PlaneCollider when componentData.Collider is { } plane:
                    component = ApplyColliderProperties(new PlaneColliderComponent
                    {
                        Size = plane.PlaneSize.ToVector2()
                    }, plane);
                    break;
                case SceneComponentType.MeshCollider when componentData.Collider is { } mesh:
                    component = ApplyColliderProperties(new MeshColliderComponent
                    {
                        Mesh = mesh.Mesh
                    }, mesh);
                    break;
                case SceneComponentType.TerrainCollider when componentData.Collider is { } terrain:
                    component = ApplyColliderProperties(new TerrainColliderComponent
                    {
                        TerrainData = terrain.TerrainData,
                        HorizontalSize = terrain.PlaneSize.ToVector2(),
                        HeightScale = terrain.HeightScale
                    }, terrain);
                    break;
                case SceneComponentType.Animator when componentData.Animator is { } animator:
                    component = new AnimatorComponent
                    {
                        AnimationSource = animator.AnimationSource,
                        DefaultClip = animator.DefaultClip ?? animator.Clip,
                        PlayAutomatically = animator.PlayAutomatically,
                        Loop = animator.Loop,
                        Speed = animator.Speed,
                        DefaultFadeDuration = animator.DefaultFadeDuration,
                        AnimationSet = animator.AnimationSet
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

    /// <summary>Encodes one concrete collider without a shape discriminator.</summary>
    /// <param name="collider">Collider to encode.</param>
    /// <returns>Persistent concrete collider record.</returns>
    private static SceneComponentData EncodeCollider(ColliderComponent collider)
    {
        var type = collider switch
        {
            BoxColliderComponent => SceneComponentType.BoxCollider,
            SphereColliderComponent => SceneComponentType.SphereCollider,
            CapsuleColliderComponent => SceneComponentType.CapsuleCollider,
            CylinderColliderComponent => SceneComponentType.CylinderCollider,
            PlaneColliderComponent => SceneComponentType.PlaneCollider,
            MeshColliderComponent => SceneComponentType.MeshCollider,
            TerrainColliderComponent => SceneComponentType.TerrainCollider,
            _ => throw new NotSupportedException(
                $"Collider type '{collider.GetType().Name}' cannot be saved.")
        };
        var data = new ColliderData(
            Center: SceneVector3.From(collider.Center),
            Size: SceneVector3.From((collider as BoxColliderComponent)?.Size ?? Vector3.One),
            Radius: collider switch
            {
                SphereColliderComponent sphere => sphere.Radius,
                CapsuleColliderComponent capsule => capsule.Radius,
                CylinderColliderComponent cylinder => cylinder.Radius,
                _ => 0.5f
            },
            Height: collider switch
            {
                CapsuleColliderComponent capsule => capsule.Height,
                CylinderColliderComponent cylinder => cylinder.Height,
                _ => 1f
            },
            IsTrigger: collider.IsTrigger,
            Friction: collider.Friction,
            Restitution: collider.Restitution,
            Mesh: (collider as MeshColliderComponent)?.Mesh,
            CollisionLayer: collider.CollisionLayer,
            CollisionMask: collider.CollisionMask,
            PlaneSize: SceneVector2.From(collider switch
            {
                PlaneColliderComponent plane => plane.Size,
                TerrainColliderComponent terrain => terrain.HorizontalSize,
                _ => Vector2.One
            }),
            TerrainData: (collider as TerrainColliderComponent)?.TerrainData,
            HeightScale: (collider as TerrainColliderComponent)?.HeightScale ?? 1f);
        return new SceneComponentData(type, collider.Enabled, Collider: data);
    }

    /// <summary>Migrates a version 8 shape-switched collider to a concrete component.</summary>
    /// <param name="data">Legacy collider record.</param>
    /// <returns>Concrete collider preserving authored values.</returns>
    private static ColliderComponent DecodeLegacyCollider(ColliderData data)
    {
        ColliderComponent collider = data.Shape switch
        {
            LegacyColliderShape.Sphere => new SphereColliderComponent { Radius = data.Radius },
            LegacyColliderShape.Capsule => new CapsuleColliderComponent
                { Radius = data.Radius, Height = data.Height },
            LegacyColliderShape.Cylinder => new CylinderColliderComponent
                { Radius = data.Radius, Height = data.Height },
            LegacyColliderShape.Plane => new PlaneColliderComponent(),
            LegacyColliderShape.Mesh => new MeshColliderComponent { Mesh = data.Mesh },
            _ => new BoxColliderComponent { Size = data.Size.ToVector3() }
        };
        return ApplyColliderProperties(collider, data);
    }

    /// <summary>Applies properties shared by every concrete collider.</summary>
    /// <typeparam name="T">Concrete collider type.</typeparam>
    /// <param name="collider">Collider receiving values.</param>
    /// <param name="data">Persistent collider values.</param>
    /// <returns>The supplied collider.</returns>
    private static T ApplyColliderProperties<T>(T collider, ColliderData data)
        where T : ColliderComponent
    {
        collider.Center = data.Center.ToVector3();
        collider.IsTrigger = data.IsTrigger;
        collider.Friction = data.Friction;
        collider.Restitution = data.Restitution;
        collider.CollisionLayer = data.CollisionLayer;
        collider.CollisionMask = data.CollisionMask;
        return collider;
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
        string? CustomType,
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
        LegacyColliderShape? Shape = null,
        SceneVector3 Center = default,
        SceneVector3 Size = default,
        float Radius = 0.5f,
        float Height = 1f,
        bool IsTrigger = false,
        float Friction = 0.5f,
        float Restitution = 0f,
        AssetReference? Mesh = null,
        uint CollisionLayer = 1u,
        uint CollisionMask = uint.MaxValue,
        SceneVector2 PlaneSize = default,
        AssetReference? TerrainData = null,
        float HeightScale = 1f);

    private sealed record AnimatorData(
        AssetReference? AnimationSource,
        string? Clip,
        bool PlayAutomatically,
        bool Loop,
        float Speed,
        float DefaultFadeDuration = 0.2f,
        string? DefaultClip = null,
        AssetReference? AnimationSet = null);

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

    private readonly record struct SceneVector2(float X, float Y)
    {
        /// <summary>Creates serializable vector data.</summary>
        /// <param name="value">Runtime vector.</param>
        /// <returns>Serializable vector data.</returns>
        public static SceneVector2 From(Vector2 value) => new(value.X, value.Y);

        /// <summary>Creates a runtime vector.</summary>
        /// <returns>Runtime vector.</returns>
        public Vector2 ToVector2() => new(X, Y);
    }

    private enum SceneNodeType
    {
        Node3D,
        Cube,
        ImportedModel,
        AssetMesh,
        PerspectiveCamera,
        Custom
    }

    private enum SceneComponentType
    {
        Script,
        RigidBody,
        Collider,
        BoxCollider,
        SphereCollider,
        CapsuleCollider,
        CylinderCollider,
        PlaneCollider,
        MeshCollider,
        TerrainCollider,
        Animator
    }

    /// <summary>Shape discriminator retained only for loading scene format version 8.</summary>
    private enum LegacyColliderShape
    {
        Box,
        Sphere,
        Capsule,
        Cylinder,
        Plane,
        Mesh
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
