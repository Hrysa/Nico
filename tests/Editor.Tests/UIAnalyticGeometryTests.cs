using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public sealed class UIAnalyticGeometryTests
{
    /// <summary>Verifies an ellipse uses one analytic quad instead of polygon tessellation.</summary>
    [Fact]
    public void AppendEllipse_EmitsOneExpandedAnalyticQuad()
    {
        using var vertices = new NativeBuffer<UIShapeVertex>();
        var command = new UIDrawCommand(
            10f, 20f, 50f, 40f, Color.White, UIDrawCommandType.Ellipse);

        SilkWindow.AppendAnalyticShapeVertices(vertices, command);

        Assert.Equal(6, vertices.Count);
        Assert.All(vertices.WrittenSpan.ToArray(), vertex =>
        {
            Assert.Equal(2f, vertex.Shape.X);
            Assert.Equal(new System.Numerics.Vector2(20f, 10f), vertex.HalfSize);
        });
        Assert.Equal(new System.Numerics.Vector3(9f, 19f, 0f),
            vertices.WrittenSpan[0].Position);
    }

    /// <summary>Verifies rounded-box radius and opacity reach every analytic vertex.</summary>
    [Fact]
    public void AppendRoundedRectangle_PreservesRadiusAndOpacity()
    {
        using var vertices = new NativeBuffer<UIShapeVertex>();
        var command = new UIDrawCommand(
            0f, 0f, 100f, 30f, Color.Red,
            UIDrawCommandType.RoundedRectangle,
            Opacity: 0.4f,
            CornerRadius: 7f);

        SilkWindow.AppendAnalyticShapeVertices(vertices, command);

        Assert.Equal(6, vertices.Count);
        Assert.All(vertices.WrittenSpan.ToArray(), vertex =>
        {
            Assert.Equal(1f, vertex.Shape.X);
            Assert.Equal(7f, vertex.Shape.Y);
            Assert.Equal(0.4f, vertex.Color.W);
        });
    }

    /// <summary>Verifies an ellipse stroke stores centerline radii and expands to its outer edge.</summary>
    [Fact]
    public void AppendEllipseStroke_UsesInsetCenterlineAndOuterFringe()
    {
        using var vertices = new NativeBuffer<UIShapeVertex>();
        var command = new UIDrawCommand(
            10f, 20f, 50f, 40f, Color.White,
            UIDrawCommandType.StrokedEllipse,
            StrokeWidth: 4f);

        SilkWindow.AppendAnalyticShapeVertices(vertices, command);

        Assert.Equal(6, vertices.Count);
        Assert.Equal(new System.Numerics.Vector2(18f, 8f),
            vertices.WrittenSpan[0].HalfSize);
        Assert.Equal(new System.Numerics.Vector2(3f, 4f),
            vertices.WrittenSpan[0].Shape);
        Assert.Equal(new System.Numerics.Vector3(9f, 19f, 0f),
            vertices.WrittenSpan[0].Position);
    }

    /// <summary>Verifies line geometry stays an oriented box with a conservative AA fringe.</summary>
    [Fact]
    public void AppendLine_UsesOrientedBoxDistanceField()
    {
        using var vertices = new NativeBuffer<UIShapeVertex>();
        var command = new UIDrawCommand(
            5f, 10f, 25f, 10f, Color.White,
            UIDrawCommandType.Line,
            StrokeWidth: 4f);

        SilkWindow.AppendAnalyticShapeVertices(vertices, command);

        Assert.Equal(6, vertices.Count);
        Assert.Equal(new System.Numerics.Vector2(10f, 2f),
            vertices.WrittenSpan[0].HalfSize);
        Assert.Equal(new System.Numerics.Vector3(4f, 7f, 0f),
            vertices.WrittenSpan[0].Position);
        Assert.Equal(new System.Numerics.Vector3(26f, 13f, 0f),
            vertices.WrittenSpan[2].Position);
    }
}
