using System.Numerics;
using Xunit;

namespace Engine.Graphics.Tests;

public class MeshPickerTests
{
    /// <summary>Verifies that a centered screen ray selects a centered cube.</summary>
    [Fact]
    public void Pick_CenteredCube_ReturnsCube()
    {
        var camera = CreateCamera();
        var cube = new MeshInstance3D(new CubeMesh());

        var picked = MeshPicker.Pick([cube], camera, 0f, 0f, 100f, 100f, new Vector2(50f, 50f));

        Assert.Same(cube, picked);
    }

    /// <summary>Verifies that overlapping bounds select the closest instance.</summary>
    [Fact]
    public void Pick_OverlappingCubes_ReturnsClosestCube()
    {
        var camera = CreateCamera();
        var closest = new MeshInstance3D(new CubeMesh());
        var farther = new MeshInstance3D(new CubeMesh()) { Position = new Vector3(0f, 0f, -3f) };

        var picked = MeshPicker.Pick([farther, closest], camera,
            0f, 0f, 100f, 100f, new Vector2(50f, 50f));

        Assert.Same(closest, picked);
    }

    /// <summary>Creates a camera facing the world origin.</summary>
    /// <returns>A configured perspective camera.</returns>
    private static PerspectiveCamera CreateCamera()
    {
        var camera = new PerspectiveCamera(aspect: 1f);
        camera.Position = new Vector3(0f, 0f, 5f);
        camera.LookAt(Vector3.Zero);
        return camera;
    }
}
