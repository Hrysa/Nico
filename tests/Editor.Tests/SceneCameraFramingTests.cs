using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

/// <summary>Tests Scene-camera framing for hierarchy targets.</summary>
public sealed class SceneCameraFramingTests
{
    /// <summary>Verifies a parent target frames the combined transformed bounds of descendant meshes.</summary>
    [Fact]
    public void TryFrame_ParentNode_FramesDescendantMeshBounds()
    {
        var root = new Node3D { Position = new Vector3(10f, 0f, 0f) };
        var mesh = new MeshInstance3D
        {
            Position = new Vector3(2f, 0f, 0f),
            LocalBounds = new MeshBounds(new Vector3(-1f), new Vector3(1f))
        };
        root.AddChild(mesh);
        var camera = new PerspectiveCamera(aspect: 16f / 9f)
        {
            Position = new Vector3(0f, 0f, 10f)
        };
        camera.LookAt(Vector3.Zero);

        Assert.True(SceneCameraFraming.TryFrame(camera, root));

        var expectedCenter = new Vector3(12f, 0f, 0f);
        Assert.True(Vector3.Dot(camera.GetForwardVector(),
            Vector3.Normalize(expectedCenter - camera.Position)) > 0.999f);
        Assert.True(Vector3.Distance(camera.Position, expectedCenter) > MathF.Sqrt(3f));
    }

    /// <summary>Verifies an empty hierarchy node focuses its world-space origin.</summary>
    [Fact]
    public void TryFrame_EmptyNode_FocusesWorldPosition()
    {
        var parent = new Node3D { Position = new Vector3(3f, 4f, 5f) };
        var target = new Node3D { Position = new Vector3(1f, 2f, 3f) };
        parent.AddChild(target);
        var camera = new PerspectiveCamera { Position = new Vector3(0f, 2f, 10f) };
        camera.LookAt(Vector3.Zero);

        Assert.True(SceneCameraFraming.TryFrame(camera, target));

        var expectedCenter = new Vector3(4f, 6f, 8f);
        Assert.True(Vector3.Dot(camera.GetForwardVector(),
            Vector3.Normalize(expectedCenter - camera.Position)) > 0.999f);
    }
}
