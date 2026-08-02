using System.Numerics;
using Editor;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public class SceneSelectionControllerTests
{
    /// <summary>Verifies an invisible camera selected through the hierarchy receives a transform gizmo.</summary>
    [Fact]
    public void Select_CameraNode_BuildsTransformGizmo()
    {
        var editorCamera = new PerspectiveCamera
        {
            Position = new Vector3(0f, 0f, 5f)
        };
        editorCamera.LookAt(Vector3.Zero);
        var gameCamera = new PerspectiveCamera
        {
            Position = Vector3.Zero
        };
        var controller = new SceneSelectionController(
            Array.Empty<MeshInstance3D>(), editorCamera,
            () => new GizmoViewport(0f, 0f, 200f, 200f));

        controller.Select(gameCamera);
        controller.Update(new Vector2(-1f, -1f));

        Assert.Same(gameCamera, controller.SelectedNode);
        Assert.NotEmpty(controller.BuildOverlay());
    }

    /// <summary>Verifies switching editing scenes clears selection from the previous graph.</summary>
    [Fact]
    public void SetObjects_RuntimeScene_ClearsAuthoredSelection()
    {
        var authoredObject = new MeshInstance3D(new CubeMesh());
        var runtimeObject = new MeshInstance3D(new CubeMesh());
        var controller = new SceneSelectionController(
            new[] { authoredObject }, new PerspectiveCamera(),
            () => new GizmoViewport(0f, 0f, 200f, 200f));
        controller.Select(authoredObject);

        controller.SetObjects(new[] { runtimeObject });

        Assert.Null(controller.SelectedNode);
        Assert.Empty(controller.BuildOverlay());
    }
}
