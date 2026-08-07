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

    /// <summary>Verifies duplicate UI keys remain unconsumed while fly mode is inactive.</summary>
    [Fact]
    public void KeyDown_RepeatedUiKeyWhileInactive_DoesNotConsumeRepeat()
    {
        var controller = new FlyCameraController(
            new PerspectiveCamera(), _ => { }, () => { });

        Assert.False(controller.KeyDown(InputKey.Right));
        Assert.False(controller.KeyDown(InputKey.Right));
    }

    /// <summary>Verifies repeated fly-mode keys remain owned by the camera context.</summary>
    [Fact]
    public void KeyDown_RepeatedMovementKeyWhileActive_RemainsConsumed()
    {
        var controller = new FlyCameraController(
            new PerspectiveCamera(), _ => { }, () => { });
        controller.KeyDown(InputKey.F);

        Assert.True(controller.KeyDown(InputKey.W));
        Assert.True(controller.KeyDown(InputKey.W));
    }

    /// <summary>Verifies losing viewport focus clears movement and releases capture.</summary>
    [Fact]
    public void ReleaseFocus_WhileActive_ReleasesCaptureAndHeldMovement()
    {
        var camera = new PerspectiveCamera();
        var captured = false;
        var controller = new FlyCameraController(
            camera, value => captured = value, () => { });
        controller.KeyDown(InputKey.F);
        controller.KeyDown(InputKey.W);
        controller.ReleaseFocus();
        var position = camera.Position;

        controller.Update(1d);

        Assert.False(controller.IsActive);
        Assert.False(captured);
        Assert.Equal(position, camera.Position);
    }
}
