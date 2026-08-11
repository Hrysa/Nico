using System.Numerics;
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

    /// <summary>Verifies an unmodified two-finger gesture rotates without translating.</summary>
    [Fact]
    public void ApplyTwoFingerGesture_WithoutShift_RotatesCamera()
    {
        var camera = new PerspectiveCamera();
        var controller = new FlyCameraController(camera, _ => { }, () => { });
        var position = camera.Position;

        controller.ApplyTwoFingerGesture(new Vector2(2f, -3f), translate: false);

        Assert.Equal(position, camera.Position);
        Assert.NotEqual(Vector3.Zero, camera.Rotation);
    }

    /// <summary>Verifies Shift changes a two-finger gesture into camera-plane movement.</summary>
    [Fact]
    public void ApplyTwoFingerGesture_WithShift_TranslatesCamera()
    {
        var camera = new PerspectiveCamera();
        var controller = new FlyCameraController(camera, _ => { }, () => { });
        var rotation = camera.Rotation;
        var position = camera.Position;

        controller.ApplyTwoFingerGesture(new Vector2(2f, -3f), translate: true);

        Assert.NotEqual(position, camera.Position);
        Assert.Equal(rotation, camera.Rotation);
    }

    /// <summary>Verifies positive pinch magnification moves toward the viewed scene.</summary>
    [Fact]
    public void ApplyPinchZoom_PositiveMagnification_MovesForward()
    {
        var camera = new PerspectiveCamera();
        var controller = new FlyCameraController(camera, _ => { }, () => { });
        var before = camera.Position;

        controller.ApplyPinchZoom(0.1f);

        Assert.True(Vector3.Dot(camera.Position - before, camera.GetForwardVector()) > 0f);
    }

    /// <summary>Verifies a positive desktop mouse-wheel step dollies toward the scene.</summary>
    [Fact]
    public void ApplyMouseWheelZoom_PositiveDelta_MovesForwardWithoutRotating()
    {
        var camera = new PerspectiveCamera();
        var controller = new FlyCameraController(camera, _ => { }, () => { });
        var beforePosition = camera.Position;
        var beforeRotation = camera.Rotation;

        controller.ApplyMouseWheelZoom(1f);

        Assert.True(Vector3.Dot(camera.Position - beforePosition,
            camera.GetForwardVector()) > 0f);
        Assert.Equal(beforeRotation, camera.Rotation);
    }

    /// <summary>Verifies desktop secondary-button motion rotates without translating.</summary>
    [Fact]
    public void ApplyMouseLook_PointerDelta_RotatesWithoutMoving()
    {
        var camera = new PerspectiveCamera();
        var controller = new FlyCameraController(camera, _ => { }, () => { });
        var position = camera.Position;

        controller.ApplyMouseLook(new Vector2(10f, -5f));

        Assert.NotEqual(Vector3.Zero, camera.Rotation);
        Assert.Equal(position, camera.Position);
    }

    /// <summary>Verifies desktop middle-button motion pans without rotating.</summary>
    [Fact]
    public void ApplyMousePan_PointerDelta_MovesWithoutRotating()
    {
        var camera = new PerspectiveCamera();
        var controller = new FlyCameraController(camera, _ => { }, () => { });
        var rotation = camera.Rotation;

        controller.ApplyMousePan(new Vector2(10f, -5f));

        Assert.NotEqual(Vector3.Zero, camera.Position);
        Assert.Equal(rotation, camera.Rotation);
    }
}
