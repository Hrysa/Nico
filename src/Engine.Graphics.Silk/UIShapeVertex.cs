using System.Numerics;

namespace Engine.Graphics;

/// <summary>Stores one analytic UI-shape vertex and its interpolated distance-field data.</summary>
internal struct UIShapeVertex : IEquatable<UIShapeVertex>
{
    /// <summary>Vertex position in logical UI coordinates.</summary>
    internal Vector3 Position;
    /// <summary>Linear RGBA shape color.</summary>
    internal Vector4 Color;
    /// <summary>Shape-local coordinate at this vertex.</summary>
    internal Vector2 LocalPosition;
    /// <summary>Half extent of the unexpanded shape.</summary>
    internal Vector2 HalfSize;
    /// <summary>Shape kind and its primary scalar parameter.</summary>
    internal Vector2 Shape;

    /// <summary>Gets the packed vertex size in bytes.</summary>
    internal static readonly uint Stride = (uint)(sizeof(float) * 13);

    /// <summary>Creates an analytic UI-shape vertex.</summary>
    /// <param name="position">Logical vertex position.</param>
    /// <param name="color">Linear RGBA color.</param>
    /// <param name="localPosition">Shape-local coordinate.</param>
    /// <param name="halfSize">Unexpanded shape half extent.</param>
    /// <param name="shapeKind">Shader shape-kind identifier.</param>
    /// <param name="parameter">Shape-specific scalar parameter.</param>
    internal UIShapeVertex(
        Vector3 position,
        Vector4 color,
        Vector2 localPosition,
        Vector2 halfSize,
        float shapeKind,
        float parameter)
    {
        Position = position;
        Color = color;
        LocalPosition = localPosition;
        HalfSize = halfSize;
        Shape = new Vector2(shapeKind, parameter);
    }

    /// <summary>Compares two analytic vertices without boxing.</summary>
    /// <param name="other">Vertex to compare.</param>
    /// <returns>True when every packed field matches.</returns>
    public readonly bool Equals(UIShapeVertex other) =>
        Position.Equals(other.Position) && Color.Equals(other.Color) &&
        LocalPosition.Equals(other.LocalPosition) && HalfSize.Equals(other.HalfSize) &&
        Shape.Equals(other.Shape);
}
