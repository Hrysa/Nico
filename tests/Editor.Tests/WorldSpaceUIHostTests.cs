using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class WorldSpaceUIHostTests
{
    /// <summary>Verifies a perspective camera maps its look target to viewport center.</summary>
    [Fact]
    public void TryProject_PerspectiveTarget_MapsToCenter()
    {
        var camera = new PerspectiveCamera(aspect: 16f / 9f)
        {
            Position = new Vector3(0f, 2f, 5f)
        };
        camera.LookAt(Vector3.Zero);

        var visible = WorldSpaceUIHost.TryProject(
            camera, Vector3.Zero, new Vector2(1280f, 720f), out var screenPosition);

        Assert.True(visible);
        Assert.InRange(screenPosition.X, 639.99f, 640.01f);
        Assert.InRange(screenPosition.Y, 359.99f, 360.01f);
    }

    /// <summary>Verifies an orthographic 2D camera preserves centered screen-space placement.</summary>
    [Fact]
    public void TryProject_OrthographicPoint_UsesTopLeftScreenCoordinates()
    {
        var camera = new OrthographicCamera(size: 10f, aspect: 1f);

        Assert.True(WorldSpaceUIHost.TryProject(
            camera, new Vector3(0f, 2.5f, 0f), new Vector2(100f, 100f), out var screenPosition));

        Assert.InRange(screenPosition.X, 49.99f, 50.01f);
        Assert.InRange(screenPosition.Y, 24.99f, 25.01f);
    }

    /// <summary>Verifies content behind a perspective camera is removed from layout and painting.</summary>
    [Fact]
    public void UpdateProjection_BehindCamera_HidesContent()
    {
        var camera = new PerspectiveCamera(aspect: 1f)
        {
            Position = Vector3.Zero,
            Rotation = Vector3.Zero
        };
        var host = new WorldSpaceUIHost { Width = 100f, Height = 100f };
        var label = new Label("Hidden", 60f, 20f);
        host.Add(label, new Vector3(0f, 0f, 1f));

        Assert.True(host.UpdateProjection(camera, new Vector2(100f, 100f)));

        Assert.False(label.IsVisible);
    }

    /// <summary>Verifies a projected child is bottom-centered above its anchor point.</summary>
    [Fact]
    public void BuildDrawList_VisibleAnchor_ArrangesBottomCenter()
    {
        var camera = new OrthographicCamera(size: 10f, aspect: 1f);
        var host = new WorldSpaceUIHost { Width = 100f, Height = 100f };
        var label = new Label("Origin", 20f, 10f);
        host.Add(label, Vector2.Zero, new Vector2(0f, -5f));
        host.UpdateProjection(camera, new Vector2(100f, 100f));

        host.BuildDrawList();

        Assert.Equal(40f, label.Left);
        Assert.Equal(35f, label.Top);
    }

    /// <summary>Verifies unchanged projection updates allocate no managed memory after warmup.</summary>
    [Fact]
    public void UpdateProjection_Unchanged_IsAllocationFree()
    {
        var camera = new OrthographicCamera(size: 10f, aspect: 1f);
        var host = new WorldSpaceUIHost();
        host.Add(new Label("Origin", 60f, 20f), Vector2.Zero);
        var viewport = new Vector2(100f, 100f);
        host.UpdateProjection(camera, viewport);
        host.UpdateProjection(camera, viewport);

        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++)
            Assert.False(host.UpdateProjection(camera, viewport));

        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
    }
}
