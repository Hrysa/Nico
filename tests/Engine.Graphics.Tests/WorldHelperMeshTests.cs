using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies procedural editor world-helper meshes.
/// </summary>
public class WorldHelperMeshTests
{
    /// <summary>
    /// Verifies that the origin helper contains one colored prism for every world axis.
    /// </summary>
    [Fact]
    public void OriginAxes_EmitsThreeColoredAxisPrisms()
    {
        var axes = new OriginAxesMesh();

        Assert.Equal(108, axes.Vertices.Length);
        Assert.Contains(axes.Vertices, vertex => vertex.Color == Color.Red.Rgb);
        Assert.Contains(axes.Vertices, vertex => vertex.Color == Color.Green.Rgb);
        Assert.Contains(axes.Vertices, vertex => vertex.Color == Color.Blue.Rgb);
    }
}
