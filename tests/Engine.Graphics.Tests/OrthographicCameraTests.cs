using System.Numerics;
using Xunit;

namespace Engine.Graphics.Tests;

public class OrthographicCameraTests
{
    /// <summary>Verifies that viewport changes update the aspect ratio.</summary>
    [Fact]
    public void UpdateViewport_UpdatesAspectRatio()
    {
        var camera = new OrthographicCamera();

        camera.UpdateViewport(800f, 400f);

        Assert.Equal(2f, camera.Aspect);
    }

    /// <summary>Verifies that panning changes the view matrix.</summary>
    [Fact]
    public void Pan_ChangesViewMatrix()
    {
        var camera = new OrthographicCamera();
        var before = camera.GetViewMatrix();

        camera.Pan(2f, 3f);

        Assert.NotEqual(before, camera.GetViewMatrix());
        Assert.Equal(new Vector3(2f, -3f, 0f), camera.Position);
    }

    /// <summary>Verifies an orthographic camera inherits parent movement.</summary>
    [Fact]
    public void ParentPositionChange_ChangesViewMatrix()
    {
        var parent = new Node3D();
        var camera = new OrthographicCamera();
        parent.AddChild(camera);
        var before = camera.GetViewMatrix();

        parent.Position = new Vector3(2f, 0f, 0f);

        Assert.NotEqual(before, camera.GetViewMatrix());
    }

    /// <summary>Verifies that positive zoom reduces the visible size.</summary>
    [Fact]
    public void Zoom_PositiveDelta_ReducesSize()
    {
        var camera = new OrthographicCamera(size: 10f);

        camera.Zoom(1f);

        Assert.Equal(9f, camera.Size, 5);
    }
}
