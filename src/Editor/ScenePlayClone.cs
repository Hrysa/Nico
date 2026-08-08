using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>
/// Creates an isolated in-memory scene graph for play mode.
/// </summary>
public static class ScenePlayClone
{
    /// <summary>
    /// Clones a scene hierarchy and resolves its active game camera in the clone.
    /// </summary>
    /// <param name="root">Authored synthetic scene root.</param>
    /// <param name="gameCamera">Authored active game camera.</param>
    /// <returns>An isolated scene suitable for runtime mutation.</returns>
    public static LoadedScene Create(Node3D root, PerspectiveCamera gameCamera)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(gameCamera);
        var meshInstances = new List<MeshInstance3D>();
        PerspectiveCamera? clonedGameCamera = null;
        var clonedRoot = new Node3D { Name = root.Name };
        CopyNodeState(root, clonedRoot);
        foreach (var child in root.Children)
            clonedRoot.AddChild(CloneNode(child, gameCamera, meshInstances, ref clonedGameCamera));
        if (clonedGameCamera is null)
            throw new InvalidOperationException("The active game camera does not belong to the scene.");
        return new LoadedScene(clonedRoot, meshInstances, clonedGameCamera);
    }

    /// <summary>
    /// Clones one supported scene node and all descendants.
    /// </summary>
    /// <param name="source">Authored node.</param>
    /// <param name="gameCamera">Authored active game camera.</param>
    /// <param name="meshInstances">Collection receiving cloned renderable nodes.</param>
    /// <param name="clonedGameCamera">Receives the cloned active camera.</param>
    /// <returns>The cloned subtree root.</returns>
    private static Node CloneNode(
        Node source,
        PerspectiveCamera gameCamera,
        ICollection<MeshInstance3D> meshInstances,
        ref PerspectiveCamera? clonedGameCamera)
    {
        Node3D clone = source switch
        {
            PerspectiveCamera camera => new PerspectiveCamera(
                camera.Fov, near: camera.Near, far: camera.Far),
            MeshInstance3D => new MeshInstance3D(),
            Node3D when source.GetType() == typeof(Node3D) => new Node3D(),
            _ => throw new NotSupportedException(
                $"Scene node type '{source.GetType().Name}' cannot enter play mode.")
        };
        CopyNodeState(source, clone);
        if (clone is MeshInstance3D clonedMesh)
            meshInstances.Add(clonedMesh);
        if (ReferenceEquals(source, gameCamera))
            clonedGameCamera = (PerspectiveCamera)clone;
        foreach (var child in source.Children)
            clone.AddChild(CloneNode(child, gameCamera, meshInstances, ref clonedGameCamera));
        return clone;
    }

    /// <summary>
    /// Copies editable node state without sharing hierarchy relationships.
    /// </summary>
    /// <param name="source">Source node.</param>
    /// <param name="destination">Destination node.</param>
    private static void CopyNodeState(Node source, Node destination)
    {
        destination.Name = source.Name;
        destination.Position = source.Position;
        destination.Orientation = source.Orientation;
        destination.Scale = source.Scale;
        var components = source.Components;
        for (var index = 0; index < components.Count; index++)
            destination.AddComponent(CloneComponent(components[index]));
        if (source is MeshInstance3D sourceMesh && destination is MeshInstance3D destinationMesh)
        {
            destinationMesh.Mesh = sourceMesh.Mesh;
            destinationMesh.LocalBounds = sourceMesh.LocalBounds;
            destinationMesh.Materials.AddRange(sourceMesh.Materials);
            destinationMesh.MaterialOverride = sourceMesh.MaterialOverride?.Clone();
        }
    }

    /// <summary>Clones one supported authored component without sharing mutable state.</summary>
    /// <param name="source">Component to clone.</param>
    /// <returns>Detached component clone.</returns>
    private static Component CloneComponent(Component source)
    {
        switch (source)
        {
            case ScriptComponent sourceScript:
                var script = new ScriptComponent(sourceScript.ScriptId)
                    { Enabled = sourceScript.Enabled };
                var overrides = sourceScript.PropertyOverrides;
                for (var index = 0; index < overrides.Count; index++)
                    script.SetPropertyOverride(overrides[index].PropertyId, overrides[index].Value);
                return script;
            case RigidBodyComponent sourceBody:
                return new RigidBodyComponent
                {
                    Enabled = sourceBody.Enabled,
                    MotionType = sourceBody.MotionType,
                    Mass = sourceBody.Mass,
                    LinearVelocity = sourceBody.LinearVelocity,
                    UseGravity = sourceBody.UseGravity,
                    GravityScale = sourceBody.GravityScale,
                    LinearDamping = sourceBody.LinearDamping
                };
            case ColliderComponent sourceCollider:
                return new ColliderComponent
                {
                    Enabled = sourceCollider.Enabled,
                    Shape = sourceCollider.Shape,
                    Center = sourceCollider.Center,
                    Size = sourceCollider.Size,
                    Radius = sourceCollider.Radius,
                    Height = sourceCollider.Height,
                    IsTrigger = sourceCollider.IsTrigger,
                    Friction = sourceCollider.Friction,
                    Restitution = sourceCollider.Restitution
                };
            case AnimatorComponent sourceAnimator:
                return new AnimatorComponent
                {
                    Enabled = sourceAnimator.Enabled,
                    Clip = sourceAnimator.Clip,
                    PlayAutomatically = sourceAnimator.PlayAutomatically,
                    Loop = sourceAnimator.Loop,
                    Speed = sourceAnimator.Speed
                };
            default:
                throw new NotSupportedException(
                    $"Component type '{source.GetType().Name}' cannot enter play mode.");
        }
    }

}
