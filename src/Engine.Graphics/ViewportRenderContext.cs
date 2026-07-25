namespace Engine.Graphics;

/// <summary>
/// Context passed to viewport render callbacks. Provides viewport
/// dimensions and an ID. The actual draw commands are recorded by
/// the rendering system internally.
/// </summary>
public class ViewportRenderContext
{
    /// <summary>Gets the viewport ID this context renders into.</summary>
    public uint ViewportId { get; init; }

    /// <summary>Gets the viewport width in pixels.</summary>
    public uint Width { get; init; }

    /// <summary>Gets the viewport height in pixels.</summary>
    public uint Height { get; init; }
}
