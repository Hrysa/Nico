using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies overlay tessellation from shared handle geometry.
/// </summary>
public class GizmoOverlayBuilderTests
{
    private static readonly GizmoViewport Viewport = new(0f, 0f, 800f, 600f);

    /// <summary>
    /// Verifies that invalid layout produces no overlay geometry.
    /// </summary>
    [Fact]
    public void Build_ReturnsEmptyForInvalidLayout()
    {
        Assert.Empty(GizmoOverlayBuilder.Build(GizmoLayoutResult.Empty, GizmoHandleKind.None, GizmoHandleKind.None));
    }

    /// <summary>
    /// Verifies that output positions are finite and contained by the Scene viewport.
    /// </summary>
    [Fact]
    public void Build_EmitsFiniteVerticesInsideViewport()
    {
        var vertices = GizmoOverlayBuilder.Build(CreateLayout(), GizmoHandleKind.None, GizmoHandleKind.None);

        Assert.NotEmpty(vertices);
        Assert.All(vertices, vertex =>
        {
            Assert.True(float.IsFinite(vertex.Position.X) && float.IsFinite(vertex.Position.Y));
            Assert.InRange(vertex.Position.X, Viewport.X, Viewport.X + Viewport.Width);
            Assert.InRange(vertex.Position.Y, Viewport.Y, Viewport.Y + Viewport.Height);
        });
    }

    /// <summary>
    /// Verifies that background rotation geometry is emitted before foreground translation geometry.
    /// </summary>
    [Fact]
    public void Build_EmitsLayerZeroBeforeLayerOne()
    {
        var layout = new GizmoLayoutResult
        {
            IsValid = true,
            Viewport = Viewport,
            Handles =
            [
                new GizmoHandleGeometry(GizmoHandleKind.TranslateY, 1, Vector3.UnitY, true,
                    [new GizmoSegment(new Vector2(100f, 120f), new Vector2(200f, 120f), 2f, 8f)], [], 100f),
                new GizmoHandleGeometry(GizmoHandleKind.RotateX, 0, Vector3.UnitX, true,
                    [new GizmoSegment(new Vector2(100f, 100f), new Vector2(200f, 100f), 2f, 8f)], [], 100f)
            ]
        };
        var vertices = GizmoOverlayBuilder.Build(layout, GizmoHandleKind.None, GizmoHandleKind.None);
        var firstGreen = Array.FindIndex(vertices, vertex => vertex.Color == new Vector3(0f, 1f, 0f));
        var lastRed = Array.FindLastIndex(vertices, vertex => vertex.Color == new Vector3(1f, 0f, 0f));

        Assert.True(firstGreen >= 0);
        Assert.True(lastRed >= 0);
        Assert.True(lastRed < firstGreen);
    }

    /// <summary>
    /// Verifies that active geometry receives the final highlight pass.
    /// </summary>
    [Fact]
    public void Build_EmitsActiveHighlightLast()
    {
        var vertices = GizmoOverlayBuilder.Build(CreateLayout(), GizmoHandleKind.RotateX, GizmoHandleKind.TranslateX);
        var highlight = new Vector3(1f, 1f, 0.5f);

        Assert.Equal(highlight, vertices[^1].Color);
        Assert.Equal(highlight, vertices[^6].Color);
    }

    /// <summary>
    /// Verifies that a thick segment emits two non-degenerate triangles with matching winding.
    /// </summary>
    [Fact]
    public void Build_SegmentTrianglesHaveConsistentWinding()
    {
        var layout = new GizmoLayoutResult
        {
            IsValid = true,
            Viewport = Viewport,
            Handles =
            [
                new GizmoHandleGeometry(GizmoHandleKind.TranslateX, 1, Vector3.UnitX, true,
                    [new GizmoSegment(new Vector2(100f, 100f), new Vector2(200f, 100f), 2f, 8f)], [], 100f)
            ]
        };

        var vertices = GizmoOverlayBuilder.Build(layout, GizmoHandleKind.None, GizmoHandleKind.None);

        Assert.Equal(6, vertices.Length);
        var firstArea = SignedArea(vertices[0].Position, vertices[1].Position, vertices[2].Position);
        var secondArea = SignedArea(vertices[3].Position, vertices[4].Position, vertices[5].Position);
        Assert.NotEqual(0f, firstArea);
        Assert.Equal(MathF.Sign(firstArea), MathF.Sign(secondArea));
    }

    /// <summary>
    /// Creates a real projected layout centered in the viewport.
    /// </summary>
    /// <returns>The generated layout.</returns>
    private static GizmoLayoutResult CreateLayout()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(5f, 4f, 6f), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 4f / 3f, 0.1f, 100f);
        projection.M22 = -projection.M22;
        return GizmoLayout.Create(Vector3.Zero, view, projection, Viewport);
    }

    /// <summary>
    /// Calculates signed double-area for a screen-space triangle.
    /// </summary>
    /// <param name="a">First vertex.</param>
    /// <param name="b">Second vertex.</param>
    /// <param name="c">Third vertex.</param>
    /// <returns>The signed double-area.</returns>
    private static float SignedArea(Vector3 a, Vector3 b, Vector3 c)
    {
        return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    }
}
