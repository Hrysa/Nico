using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Identifies a semantic gizmo interaction handle.
/// </summary>
public enum GizmoHandleKind
{
    None,
    RotateX,
    RotateY,
    RotateZ,
    TranslateX,
    TranslateY,
    TranslateZ
}

/// <summary>
/// Describes a viewport rectangle in screen pixels.
/// </summary>
/// <param name="X">Left screen coordinate.</param>
/// <param name="Y">Top screen coordinate.</param>
/// <param name="Width">Viewport width.</param>
/// <param name="Height">Viewport height.</param>
public readonly record struct GizmoViewport(float X, float Y, float Width, float Height)
{
    /// <summary>
    /// Gets whether the viewport has finite positive dimensions.
    /// </summary>
    internal bool IsValid => float.IsFinite(X) && float.IsFinite(Y)
        && float.IsFinite(Width) && float.IsFinite(Height)
        && Width > 0f && Height > 0f;

    /// <summary>
    /// Determines whether a screen point is inside the viewport.
    /// </summary>
    /// <param name="point">Screen point.</param>
    /// <returns>True when the point lies inside the viewport bounds.</returns>
    internal bool Contains(Vector2 point)
    {
        return IsValid && point.X >= X && point.X <= X + Width
            && point.Y >= Y && point.Y <= Y + Height;
    }
}

/// <summary>
/// Contains the editable parts of a selected object's transform.
/// </summary>
/// <param name="Position">World position.</param>
/// <param name="Rotation">Euler rotation in radians.</param>
public readonly record struct GizmoTransform(Vector3 Position, Vector3 Rotation);

/// <summary>
/// Describes a clipped screen-space line primitive.
/// </summary>
/// <param name="Start">Start point in screen pixels.</param>
/// <param name="End">End point in screen pixels.</param>
/// <param name="VisibleWidth">Rendered width in pixels.</param>
/// <param name="HitWidth">Pick width in pixels.</param>
internal readonly record struct GizmoSegment(Vector2 Start, Vector2 End, float VisibleWidth, float HitWidth);

/// <summary>
/// Describes a clipped screen-space triangle primitive.
/// </summary>
/// <param name="A">First vertex.</param>
/// <param name="B">Second vertex.</param>
/// <param name="C">Third vertex.</param>
internal readonly record struct GizmoTriangle(Vector2 A, Vector2 B, Vector2 C);

/// <summary>
/// Stores all shared render and hit geometry for one semantic handle.
/// </summary>
internal readonly record struct GizmoHandleGeometry(
    GizmoHandleKind Kind,
    int Layer,
    Vector3 Color,
    bool Interactive,
    IReadOnlyList<GizmoSegment> Segments,
    IReadOnlyList<GizmoTriangle> Triangles,
    float ScreenExtent);

/// <summary>
/// Stores one validated gizmo layout and the camera state that produced it.
/// </summary>
internal sealed class GizmoLayoutResult
{
    /// <summary>Gets a reusable invalid layout.</summary>
    internal static GizmoLayoutResult Empty { get; } = new();

    /// <summary>Gets whether this layout can be rendered and interacted with.</summary>
    internal bool IsValid { get; init; }

    /// <summary>Gets the layout viewport.</summary>
    internal GizmoViewport Viewport { get; init; }

    /// <summary>Gets the captured view matrix.</summary>
    internal Matrix4x4 View { get; init; }

    /// <summary>Gets the captured projection matrix.</summary>
    internal Matrix4x4 Projection { get; init; }

    /// <summary>Gets the target world position.</summary>
    internal Vector3 TargetWorld { get; init; }

    /// <summary>Gets the target screen position.</summary>
    internal Vector2 TargetScreen { get; init; }

    /// <summary>Gets the world distance represented by one screen pixel at the target.</summary>
    internal float WorldUnitsPerPixel { get; init; }

    /// <summary>Gets handles ordered from background to foreground.</summary>
    internal IReadOnlyList<GizmoHandleGeometry> Handles { get; init; } = [];
}
