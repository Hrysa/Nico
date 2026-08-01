using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Camera matrices used to reconstruct the procedural ground grid in world space.
/// </summary>
public struct GridPushConstants
{
    /// <summary>Transforms world-space positions into Vulkan clip space.</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>Transforms Vulkan clip-space positions back into world space.</summary>
    public Matrix4x4 InverseViewProjection;
}
