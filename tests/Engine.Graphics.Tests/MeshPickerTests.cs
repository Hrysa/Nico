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
        var cube = CreateCube();

        var picked = MeshPicker.Pick([cube], camera, 0f, 0f, 100f, 100f, new Vector2(50f, 50f));

        Assert.Same(cube, picked);
    }

    /// <summary>Verifies that overlapping bounds select the closest instance.</summary>
    [Fact]
    public void Pick_OverlappingCubes_ReturnsClosestCube()
    {
        var camera = CreateCamera();
        var closest = CreateCube();
        var farther = CreateCube();
        farther.Position = new Vector3(0f, 0f, -3f);

        var picked = MeshPicker.Pick([farther, closest], camera,
            0f, 0f, 100f, 100f, new Vector2(50f, 50f));

        Assert.Same(closest, picked);
    }

    /// <summary>Verifies imported instances can be picked from their decoded local bounds.</summary>
    [Fact]
    public void Pick_ImportedMeshBounds_ReturnsImportedInstance()
    {
        var camera = CreateCamera();
        var imported = new MeshInstance3D
        {
            Mesh = new Engine.Core.AssetReference(Engine.Core.AssetId.New(), "mesh/0"),
            LocalBounds = new MeshBounds(new Vector3(-1f), new Vector3(1f))
        };

        var picked = MeshPicker.Pick([imported], camera,
            0f, 0f, 100f, 100f, new Vector2(50f, 50f));

        Assert.Same(imported, picked);
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

    /// <summary>Creates a selectable built-in cube with decoded bounds.</summary>
    /// <returns>A built-in cube instance.</returns>
    private static MeshInstance3D CreateCube()
    {
        return new MeshInstance3D
        {
            LocalBounds = new MeshBounds(new Vector3(-0.5f), new Vector3(0.5f))
        };
    }
}
