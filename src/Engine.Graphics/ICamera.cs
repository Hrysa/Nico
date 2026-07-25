using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Abstraction for cameras that produce View and Projection matrices
/// for the rendering pipeline. Implementations generate push constants
/// compatible with the MVP vertex shader.
/// </summary>
public interface ICamera
{
    /// <summary>Gets the camera's view matrix (world-to-eye transform).</summary>
    /// <returns>The 4x4 view matrix.</returns>
    Matrix4x4 GetViewMatrix();

    /// <summary>Gets the camera's projection matrix (eye-to-clip transform).</summary>
    /// <returns>The 4x4 projection matrix.</returns>
    Matrix4x4 GetProjectionMatrix();

    /// <summary>
    /// Builds a complete PushConstants struct with Model, View, and Projection
    /// matrices ready for the vertex shader.
    /// </summary>
    /// <param name="model">The model (object-to-world) matrix.</param>
    /// <returns>A PushConstants struct with all three MVP matrices set.</returns>
    PushConstants GetPushConstants(Matrix4x4 model);

    /// <summary>
    /// Updates the camera's viewport dimensions. Called when the viewport
    /// resizes so aspect-ratio-dependent cameras can recompute projection.
    /// </summary>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    void UpdateViewport(float width, float height);
}
