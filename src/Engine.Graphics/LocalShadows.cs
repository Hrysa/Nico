using System.Numerics;

namespace Engine.Graphics;

/// <summary>Contains up to six world-to-shadow transforms for one local light.</summary>
public readonly record struct LocalShadowMatrices
{
    private readonly Matrix4x4 _face0;
    private readonly Matrix4x4 _face1;
    private readonly Matrix4x4 _face2;
    private readonly Matrix4x4 _face3;
    private readonly Matrix4x4 _face4;
    private readonly Matrix4x4 _face5;

    /// <summary>Creates a fixed local-light transform set.</summary>
    /// <param name="face0">Spot transform or positive-X point face.</param>
    /// <param name="face1">Negative-X point face.</param>
    /// <param name="face2">Positive-Y point face.</param>
    /// <param name="face3">Negative-Y point face.</param>
    /// <param name="face4">Positive-Z point face.</param>
    /// <param name="face5">Negative-Z point face.</param>
    /// <param name="count">One for spot lights or six for point lights.</param>
    internal LocalShadowMatrices(
        Matrix4x4 face0,
        Matrix4x4 face1,
        Matrix4x4 face2,
        Matrix4x4 face3,
        Matrix4x4 face4,
        Matrix4x4 face5,
        int count)
    {
        _face0 = face0;
        _face1 = face1;
        _face2 = face2;
        _face3 = face3;
        _face4 = face4;
        _face5 = face5;
        Count = count;
    }

    /// <summary>Gets the number of populated transforms.</summary>
    public int Count { get; }

    /// <summary>Gets one face transform.</summary>
    /// <param name="index">Face index.</param>
    /// <returns>Selected world-to-shadow transform.</returns>
    public Matrix4x4 GetMatrix(int index) => index switch
    {
        0 => _face0,
        1 => _face1,
        2 => _face2,
        3 => _face3,
        4 => _face4,
        5 => _face5,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

/// <summary>Builds perspective shadow cameras for point and spot lights.</summary>
public static class LocalShadowMatrixCalculator
{
    private const float MinimumNearPlane = 0.01f;

    /// <summary>Builds one cone transform for a spot light.</summary>
    /// <param name="light">Collected spotlight data.</param>
    /// <returns>One populated world-to-shadow transform.</returns>
    public static LocalShadowMatrices CalculateSpot(SceneLight light)
    {
        Validate(light, SceneLightType.Spot);
        var direction = Vector3.Normalize(light.Direction);
        var view = Matrix4x4.CreateLookAt(light.Position,
            light.Position + direction, SelectUp(direction));
        var outerAngle = MathF.Acos(Math.Clamp(light.OuterConeCosine, -1f, 1f));
        var projection = CreateProjection(outerAngle * 2f, light.Range);
        return new LocalShadowMatrices(view * projection, default, default,
            default, default, default, 1);
    }

    /// <summary>Builds six cube-face transforms for a point light.</summary>
    /// <param name="light">Collected point-light data.</param>
    /// <returns>Six populated world-to-shadow transforms.</returns>
    public static LocalShadowMatrices CalculatePoint(SceneLight light)
    {
        Validate(light, SceneLightType.Point);
        var projection = CreateProjection(MathF.PI * 0.5f, light.Range);
        var position = light.Position;
        return new LocalShadowMatrices(
            CreateFace(position, Vector3.UnitX, -Vector3.UnitY, projection),
            CreateFace(position, -Vector3.UnitX, -Vector3.UnitY, projection),
            CreateFace(position, Vector3.UnitY, Vector3.UnitZ, projection),
            CreateFace(position, -Vector3.UnitY, -Vector3.UnitZ, projection),
            CreateFace(position, Vector3.UnitZ, -Vector3.UnitY, projection),
            CreateFace(position, -Vector3.UnitZ, -Vector3.UnitY, projection),
            6);
    }

    /// <summary>Creates a Vulkan-corrected finite perspective projection.</summary>
    /// <param name="fieldOfView">Vertical field of view in radians.</param>
    /// <param name="range">Light range used as the far plane.</param>
    /// <returns>Perspective transform.</returns>
    private static Matrix4x4 CreateProjection(float fieldOfView, float range)
    {
        var nearPlane = MathF.Min(MathF.Max(range * 0.002f, MinimumNearPlane),
            range * 0.25f);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            fieldOfView, 1f, nearPlane, range);
        projection.M22 = -projection.M22;
        return projection;
    }

    /// <summary>Creates one cube-face view-projection transform.</summary>
    /// <param name="position">Light position.</param>
    /// <param name="direction">Face direction.</param>
    /// <param name="up">Face up vector.</param>
    /// <param name="projection">Shared point-light projection.</param>
    /// <returns>World-to-face transform.</returns>
    private static Matrix4x4 CreateFace(
        Vector3 position,
        Vector3 direction,
        Vector3 up,
        Matrix4x4 projection) =>
        Matrix4x4.CreateLookAt(position, position + direction, up) * projection;

    /// <summary>Selects a stable view up vector for one spotlight direction.</summary>
    /// <param name="direction">Normalized emission direction.</param>
    /// <returns>Nonparallel view up vector.</returns>
    private static Vector3 SelectUp(Vector3 direction) =>
        MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.98f
            ? Vector3.UnitX : Vector3.UnitY;

    /// <summary>Validates local-light input before matrix construction.</summary>
    /// <param name="light">Collected light.</param>
    /// <param name="expectedType">Required local-light type.</param>
    private static void Validate(SceneLight light, SceneLightType expectedType)
    {
        if (light.Type != expectedType)
            throw new ArgumentException($"A {expectedType} light is required.", nameof(light));
        if (!float.IsFinite(light.Range) || light.Range <= 0f)
            throw new ArgumentOutOfRangeException(nameof(light));
        if (expectedType == SceneLightType.Spot &&
            (!IsFinite(light.Direction) || light.Direction.LengthSquared() < 1e-8f))
        {
            throw new ArgumentOutOfRangeException(nameof(light));
        }
    }

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when all components are finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
