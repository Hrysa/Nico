using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies deterministic picking against shared layered geometry.
/// </summary>
public class GizmoPickerTests
{
    private static readonly GizmoViewport Viewport = new(0f, 0f, 100f, 100f);

    /// <summary>
    /// Verifies that foreground translation wins when its actual geometry overlaps a ring.
    /// </summary>
    [Fact]
    public void Pick_ForegroundTranslationWinsTrueOverlap()
    {
        var layout = CreateLayout(
            CreateSegmentHandle(GizmoHandleKind.RotateZ, 0, new Vector2(10f, 50f), new Vector2(90f, 50f)),
            CreateTriangleHandle(GizmoHandleKind.TranslateX, 1, new Vector2(50f, 45f), new Vector2(60f, 50f), new Vector2(50f, 55f)));

        var picked = GizmoPicker.Pick(layout, new Vector2(55f, 50f));

        Assert.Equal(GizmoHandleKind.TranslateX, picked);
    }

    /// <summary>
    /// Verifies that the background ring remains available outside foreground geometry.
    /// </summary>
    [Fact]
    public void Pick_RotationWinsOutsideTranslationGeometry()
    {
        var layout = CreateLayout(
            CreateSegmentHandle(GizmoHandleKind.RotateZ, 0, new Vector2(10f, 50f), new Vector2(90f, 50f)),
            CreateTriangleHandle(GizmoHandleKind.TranslateX, 1, new Vector2(50f, 45f), new Vector2(60f, 50f), new Vector2(50f, 55f)));

        var picked = GizmoPicker.Pick(layout, new Vector2(30f, 52f));

        Assert.Equal(GizmoHandleKind.RotateZ, picked);
    }

    /// <summary>
    /// Verifies that disabled geometry never captures input.
    /// </summary>
    [Fact]
    public void Pick_IgnoresNonInteractiveHandle()
    {
        var disabled = CreateSegmentHandle(GizmoHandleKind.TranslateX, 1, new Vector2(10f, 50f), new Vector2(90f, 50f), false);
        var layout = CreateLayout(disabled);

        Assert.Equal(GizmoHandleKind.None, GizmoPicker.Pick(layout, new Vector2(50f, 50f)));
    }

    /// <summary>
    /// Verifies that pointers outside the layout viewport are inert.
    /// </summary>
    [Fact]
    public void Pick_RejectsPointerOutsideViewport()
    {
        var layout = CreateLayout(CreateSegmentHandle(GizmoHandleKind.TranslateX, 1, new Vector2(0f, 50f), new Vector2(100f, 50f)));

        Assert.Equal(GizmoHandleKind.None, GizmoPicker.Pick(layout, new Vector2(101f, 50f)));
    }

    /// <summary>
    /// Verifies nearest-geometry selection inside one interaction layer.
    /// </summary>
    [Fact]
    public void Pick_ChoosesNearestHandleWithinLayer()
    {
        var layout = CreateLayout(
            CreateSegmentHandle(GizmoHandleKind.TranslateX, 1, new Vector2(10f, 45f), new Vector2(90f, 45f)),
            CreateSegmentHandle(GizmoHandleKind.TranslateY, 1, new Vector2(10f, 52f), new Vector2(90f, 52f)));

        Assert.Equal(GizmoHandleKind.TranslateY, GizmoPicker.Pick(layout, new Vector2(50f, 50f)));
    }

    /// <summary>
    /// Verifies stable layout ordering when geometric distances are equal.
    /// </summary>
    [Fact]
    public void Pick_PreservesLayoutOrderForExactTie()
    {
        var layout = CreateLayout(
            CreateSegmentHandle(GizmoHandleKind.TranslateX, 1, new Vector2(10f, 48f), new Vector2(90f, 48f)),
            CreateSegmentHandle(GizmoHandleKind.TranslateY, 1, new Vector2(10f, 52f), new Vector2(90f, 52f)));

        Assert.Equal(GizmoHandleKind.TranslateX, GizmoPicker.Pick(layout, new Vector2(50f, 50f)));
    }

    /// <summary>
    /// Creates a valid synthetic layout.
    /// </summary>
    /// <param name="handles">Ordered handles.</param>
    /// <returns>The test layout.</returns>
    private static GizmoLayoutResult CreateLayout(params GizmoHandleGeometry[] handles)
    {
        return new GizmoLayoutResult { IsValid = true, Viewport = Viewport, Handles = handles };
    }

    /// <summary>
    /// Creates a synthetic segment handle.
    /// </summary>
    /// <param name="kind">Handle identity.</param>
    /// <param name="layer">Interaction layer.</param>
    /// <param name="start">Segment start.</param>
    /// <param name="end">Segment end.</param>
    /// <param name="interactive">Whether input is enabled.</param>
    /// <returns>The test handle.</returns>
    private static GizmoHandleGeometry CreateSegmentHandle(GizmoHandleKind kind, int layer, Vector2 start, Vector2 end, bool interactive = true)
    {
        return new GizmoHandleGeometry(kind, layer, Vector3.One, interactive, [new GizmoSegment(start, end, 2f, 8f)], [], Vector2.Distance(start, end));
    }

    /// <summary>
    /// Creates a synthetic triangular handle.
    /// </summary>
    /// <param name="kind">Handle identity.</param>
    /// <param name="layer">Interaction layer.</param>
    /// <param name="a">First triangle vertex.</param>
    /// <param name="b">Second triangle vertex.</param>
    /// <param name="c">Third triangle vertex.</param>
    /// <returns>The test handle.</returns>
    private static GizmoHandleGeometry CreateTriangleHandle(GizmoHandleKind kind, int layer, Vector2 a, Vector2 b, Vector2 c)
    {
        return new GizmoHandleGeometry(kind, layer, Vector3.One, true, [], [new GizmoTriangle(a, b, c)], 10f);
    }
}
