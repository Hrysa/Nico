using Editor;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public class FlyCameraControllerTests
{
    /// <summary>Verifies fly mode capture and forward movement.</summary>
    [Fact]
    public void Update_WhileForwardPressed_MovesCamera()
    {
        var camera = new PerspectiveCamera();
        var captured = false;
        var controller = new FlyCameraController(camera, value => captured = value, () => { });
        var original = camera.Position;
        controller.KeyDown(InputKey.F);
        controller.KeyDown(InputKey.W);

        controller.Update(1d);

        Assert.True(captured);
        Assert.NotEqual(original, camera.Position);
    }

    /// <summary>Verifies D and A move right and left respectively.</summary>
    [Fact]
    public void Update_StrafeKeys_MoveInExpectedDirections()
    {
        var rightCamera = new PerspectiveCamera { Position = default, Rotation = default };
        var rightController = new FlyCameraController(rightCamera, _ => { }, () => { });
        rightController.KeyDown(InputKey.F);
        rightController.KeyDown(InputKey.D);
        rightController.Update(1d);

        var leftCamera = new PerspectiveCamera { Position = default, Rotation = default };
        var leftController = new FlyCameraController(leftCamera, _ => { }, () => { });
        leftController.KeyDown(InputKey.F);
        leftController.KeyDown(InputKey.A);
        leftController.Update(1d);

        Assert.True(rightCamera.Position.X > 0f);
        Assert.True(leftCamera.Position.X < 0f);
    }

    /// <summary>Verifies Escape exits fly mode and releases capture.</summary>
    [Fact]
    public void Escape_WhileActive_ReleasesCapture()
    {
        var camera = new PerspectiveCamera();
        var captured = false;
        var controller = new FlyCameraController(camera, value => captured = value, () => { });
        controller.KeyDown(InputKey.F);

        controller.KeyDown(InputKey.Escape);

        Assert.False(controller.IsActive);
        Assert.False(captured);
    }
}
