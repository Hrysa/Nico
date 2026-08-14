using System.Numerics;

namespace Engine.Graphics;

/// <summary>Describes the camera state required by view-dependent render passes.</summary>
public readonly record struct RenderCameraData
{
    /// <summary>Gets the world-to-view transform.</summary>
    public Matrix4x4 View { get; }

    /// <summary>Gets the Vulkan-corrected view-to-clip transform.</summary>
    public Matrix4x4 Projection { get; }

    /// <summary>Gets whether both transforms are finite and invertible.</summary>
    public bool IsValid { get; }

    /// <summary>Creates validated render-camera state.</summary>
    /// <param name="view">World-to-view transform.</param>
    /// <param name="projection">Vulkan-corrected view-to-clip transform.</param>
    /// <returns>Validated camera state.</returns>
    public static RenderCameraData Create(Matrix4x4 view, Matrix4x4 projection)
    {
        if (!IsFinite(view))
            throw new ArgumentOutOfRangeException(nameof(view));
        if (!IsFinite(projection))
            throw new ArgumentOutOfRangeException(nameof(projection));
        if (!Matrix4x4.Invert(view, out _) ||
            !Matrix4x4.Invert(view * projection, out _))
        {
            throw new ArgumentException("Render camera transforms must be invertible.");
        }
        return new RenderCameraData(view, projection, true);
    }

    /// <summary>Creates validated camera state.</summary>
    /// <param name="view">World-to-view transform.</param>
    /// <param name="projection">Vulkan-corrected view-to-clip transform.</param>
    /// <param name="isValid">Whether the state is usable.</param>
    private RenderCameraData(Matrix4x4 view, Matrix4x4 projection, bool isValid)
    {
        View = view;
        Projection = projection;
        IsValid = isValid;
    }

    /// <summary>Checks whether every matrix component is finite.</summary>
    /// <param name="matrix">Matrix to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Matrix4x4 matrix)
    {
        return float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
            float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
            float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
            float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
            float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
            float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
            float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
            float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
    }
}

/// <summary>Contains four stable light transforms and their camera-depth boundaries.</summary>
public readonly record struct DirectionalShadowCascades
{
    /// <summary>Gets the first cascade transform.</summary>
    public Matrix4x4 Cascade0 { get; }
    /// <summary>Gets the second cascade transform.</summary>
    public Matrix4x4 Cascade1 { get; }
    /// <summary>Gets the third cascade transform.</summary>
    public Matrix4x4 Cascade2 { get; }
    /// <summary>Gets the fourth cascade transform.</summary>
    public Matrix4x4 Cascade3 { get; }
    /// <summary>Gets the inclusive camera-depth limit for each cascade.</summary>
    public Vector4 SplitDistances { get; }
    /// <summary>Gets one world-space shadow texel size per cascade.</summary>
    public Vector4 WorldTexelSizes { get; }
    /// <summary>Gets the camera's normalized world-space forward direction.</summary>
    public Vector3 CameraForward { get; }
    /// <summary>Gets the camera world-space position.</summary>
    public Vector3 CameraPosition { get; }
    /// <summary>Gets the number of populated cascades.</summary>
    public int Count { get; }

    /// <summary>Creates computed cascade output.</summary>
    /// <param name="cascade0">First light transform.</param>
    /// <param name="cascade1">Second light transform.</param>
    /// <param name="cascade2">Third light transform.</param>
    /// <param name="cascade3">Fourth light transform.</param>
    /// <param name="splitDistances">Camera-depth limits.</param>
    /// <param name="worldTexelSizes">World-space texel sizes.</param>
    /// <param name="cameraForward">Normalized camera forward direction.</param>
    /// <param name="cameraPosition">Camera world-space position.</param>
    /// <param name="count">Populated cascade count.</param>
    internal DirectionalShadowCascades(
        Matrix4x4 cascade0,
        Matrix4x4 cascade1,
        Matrix4x4 cascade2,
        Matrix4x4 cascade3,
        Vector4 splitDistances,
        Vector4 worldTexelSizes,
        Vector3 cameraForward,
        Vector3 cameraPosition,
        int count)
    {
        Cascade0 = cascade0;
        Cascade1 = cascade1;
        Cascade2 = cascade2;
        Cascade3 = cascade3;
        SplitDistances = splitDistances;
        WorldTexelSizes = worldTexelSizes;
        CameraForward = cameraForward;
        CameraPosition = cameraPosition;
        Count = count;
    }

    /// <summary>Gets one light transform by cascade index.</summary>
    /// <param name="index">Cascade index from zero through three.</param>
    /// <returns>Selected light transform.</returns>
    public Matrix4x4 GetMatrix(int index) => index switch
    {
        0 => Cascade0,
        1 => Cascade1,
        2 => Cascade2,
        3 => Cascade3,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

/// <summary>Fits stable directional-light cascades to camera-frustum slices.</summary>
public static class DirectionalShadowCascadeCalculator
{
    /// <summary>Builds stable frustum-fitted cascades without allocating managed memory.</summary>
    /// <param name="camera">Explicit render camera.</param>
    /// <param name="directionToLight">Normalized world direction from surfaces to the light.</param>
    /// <param name="settings">SRP-authored cascade settings.</param>
    /// <param name="cascadeResolution">Square texel resolution of each atlas tile.</param>
    /// <returns>Transforms, split depths, and filtering scale for all active cascades.</returns>
    public static DirectionalShadowCascades Calculate(
        RenderCameraData camera,
        Vector3 directionToLight,
        DirectionalShadowSettings settings,
        int cascadeResolution)
    {
        if (!camera.IsValid)
            throw new ArgumentException("A valid render camera is required.", nameof(camera));
        if (!IsFinite(directionToLight) || directionToLight.LengthSquared() < 1e-8f)
            throw new ArgumentOutOfRangeException(nameof(directionToLight));
        if (!settings.IsEnabled)
            throw new ArgumentException("Enabled shadow settings are required.", nameof(settings));
        if (cascadeResolution <= 0)
            throw new ArgumentOutOfRangeException(nameof(cascadeResolution));

        Matrix4x4.Invert(camera.View, out var inverseView);
        Matrix4x4.Invert(camera.View * camera.Projection, out var inverseViewProjection);
        var cameraPosition = inverseView.Translation;
        var cameraForward = Vector3.Normalize(
            Vector3.TransformNormal(-Vector3.UnitZ, inverseView));
        Span<Vector3> nearCorners = stackalloc Vector3[4];
        Span<Vector3> farCorners = stackalloc Vector3[4];
        UnprojectCorners(inverseViewProjection, nearCorners, farCorners);
        var nearDistance = AverageViewDepth(nearCorners, camera.View);
        var cameraFarDistance = AverageViewDepth(farCorners, camera.View);
        var farDistance = MathF.Min(settings.MaxDistance, cameraFarDistance);
        if (!float.IsFinite(nearDistance) || !float.IsFinite(farDistance) ||
            farDistance <= nearDistance)
        {
            throw new ArgumentException("Camera clipping planes do not contain shadow coverage.",
                nameof(camera));
        }

        Span<float> splits = stackalloc float[4];
        for (var index = 0; index < settings.CascadeCount; index++)
        {
            var amount = (index + 1f) / settings.CascadeCount;
            var logarithmic = nearDistance *
                MathF.Pow(farDistance / nearDistance, amount);
            var uniform = nearDistance + (farDistance - nearDistance) * amount;
            splits[index] = uniform + (logarithmic - uniform) * settings.SplitLambda;
        }
        for (var index = settings.CascadeCount; index < 4; index++)
            splits[index] = farDistance;

        Span<Matrix4x4> matrices = stackalloc Matrix4x4[4];
        Span<float> texelSizes = stackalloc float[4];
        var sliceNear = nearDistance;
        for (var index = 0; index < settings.CascadeCount; index++)
        {
            FitCascade(nearCorners, farCorners, nearDistance, cameraFarDistance,
                sliceNear, splits[index], directionToLight, settings.MaxDistance,
                cascadeResolution, out matrices[index], out texelSizes[index]);
            sliceNear = splits[index];
        }
        for (var index = settings.CascadeCount; index < 4; index++)
        {
            matrices[index] = matrices[settings.CascadeCount - 1];
            texelSizes[index] = texelSizes[settings.CascadeCount - 1];
        }
        return new DirectionalShadowCascades(
            matrices[0], matrices[1], matrices[2], matrices[3],
            new Vector4(splits[0], splits[1], splits[2], splits[3]),
            new Vector4(texelSizes[0], texelSizes[1], texelSizes[2], texelSizes[3]),
            cameraForward, cameraPosition, settings.CascadeCount);
    }

    /// <summary>Unprojects Vulkan near and far clip corners.</summary>
    /// <param name="inverseViewProjection">Clip-to-world transform.</param>
    /// <param name="nearCorners">Four-corner near destination.</param>
    /// <param name="farCorners">Four-corner far destination.</param>
    private static void UnprojectCorners(
        Matrix4x4 inverseViewProjection,
        Span<Vector3> nearCorners,
        Span<Vector3> farCorners)
    {
        var index = 0;
        for (var y = -1; y <= 1; y += 2)
        {
            for (var x = -1; x <= 1; x += 2)
            {
                nearCorners[index] = Unproject(new Vector3(x, y, 0f), inverseViewProjection);
                farCorners[index] = Unproject(new Vector3(x, y, 1f), inverseViewProjection);
                index++;
            }
        }
    }

    /// <summary>Transforms one normalized-device point into world space.</summary>
    /// <param name="point">Vulkan normalized-device coordinate.</param>
    /// <param name="inverseViewProjection">Clip-to-world transform.</param>
    /// <returns>World-space point.</returns>
    private static Vector3 Unproject(Vector3 point, Matrix4x4 inverseViewProjection)
    {
        var homogeneous = Vector4.Transform(new Vector4(point, 1f), inverseViewProjection);
        if (MathF.Abs(homogeneous.W) <= 1e-8f)
            throw new ArgumentException("Camera projection produced a point at infinity.");
        return new Vector3(homogeneous.X, homogeneous.Y, homogeneous.Z) / homogeneous.W;
    }

    /// <summary>Calculates average positive camera-space depth.</summary>
    /// <param name="corners">World-space frustum corners.</param>
    /// <param name="view">World-to-view transform.</param>
    /// <returns>Average positive view depth.</returns>
    private static float AverageViewDepth(ReadOnlySpan<Vector3> corners, Matrix4x4 view)
    {
        var total = 0f;
        for (var index = 0; index < corners.Length; index++)
            total += -Vector3.Transform(corners[index], view).Z;
        return total / corners.Length;
    }

    /// <summary>Fits and texel-snaps one orthographic cascade.</summary>
    /// <param name="cameraNearCorners">Camera near-plane corners.</param>
    /// <param name="cameraFarCorners">Camera far-plane corners.</param>
    /// <param name="cameraNear">Camera near depth.</param>
    /// <param name="cameraFar">Camera far depth.</param>
    /// <param name="sliceNear">Cascade near depth.</param>
    /// <param name="sliceFar">Cascade far depth.</param>
    /// <param name="directionToLight">World direction toward the light.</param>
    /// <param name="shadowDistance">Caster padding scale.</param>
    /// <param name="cascadeResolution">Cascade texel resolution.</param>
    /// <param name="matrix">Stable world-to-shadow output.</param>
    /// <param name="worldTexelSize">World-space output texel size.</param>
    private static void FitCascade(
        ReadOnlySpan<Vector3> cameraNearCorners,
        ReadOnlySpan<Vector3> cameraFarCorners,
        float cameraNear,
        float cameraFar,
        float sliceNear,
        float sliceFar,
        Vector3 directionToLight,
        float shadowDistance,
        int cascadeResolution,
        out Matrix4x4 matrix,
        out float worldTexelSize)
    {
        var inverseRange = 1f / (cameraFar - cameraNear);
        var nearAmount = (sliceNear - cameraNear) * inverseRange;
        var farAmount = (sliceFar - cameraNear) * inverseRange;
        Span<Vector3> corners = stackalloc Vector3[8];
        var center = Vector3.Zero;
        for (var index = 0; index < 4; index++)
        {
            var edge = cameraFarCorners[index] - cameraNearCorners[index];
            corners[index] = cameraNearCorners[index] + edge * nearAmount;
            corners[index + 4] = cameraNearCorners[index] + edge * farAmount;
            center += corners[index] + corners[index + 4];
        }
        center /= 8f;
        var radius = 0f;
        for (var index = 0; index < corners.Length; index++)
            radius = MathF.Max(radius, Vector3.Distance(center, corners[index]));
        radius = MathF.Ceiling(radius * 16f) / 16f;
        var diameter = MathF.Max(radius * 2f, 0.001f);
        worldTexelSize = diameter / cascadeResolution;

        directionToLight = Vector3.Normalize(directionToLight);
        var up = MathF.Abs(Vector3.Dot(directionToLight, Vector3.UnitY)) > 0.95f
            ? Vector3.UnitZ : Vector3.UnitY;
        var eyeDistance = radius + shadowDistance;
        var view = Matrix4x4.CreateLookAt(
            center + directionToLight * eyeDistance, center, up);
        var projection = Matrix4x4.CreateOrthographic(
            diameter, diameter, 0.1f, eyeDistance + radius + shadowDistance);
        projection.M22 = -projection.M22;
        var unsnapped = view * projection;
        var origin = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), unsnapped) *
            (cascadeResolution * 0.5f);
        var roundedOrigin = new Vector2(MathF.Round(origin.X), MathF.Round(origin.Y));
        var rounding = roundedOrigin - new Vector2(origin.X, origin.Y);
        projection.M41 += rounding.X * 2f / cascadeResolution;
        projection.M42 += rounding.Y * 2f / cascadeResolution;
        matrix = view * projection;
    }

    /// <summary>Checks whether a vector is finite.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when all components are finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
