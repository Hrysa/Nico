using Engine.Core;

namespace Engine.Graphics;

/// <summary>
/// A mesh resource containing vertex data for rendering.
/// </summary>
public class Mesh : Resource
{
    /// <summary>Gets or sets the vertex data.</summary>
    public Vertex[] Vertices { get; set; } = [];
}
