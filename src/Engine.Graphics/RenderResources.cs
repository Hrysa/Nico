namespace Engine.Graphics;

/// <summary>Identifies a renderer-owned mesh resource.</summary>
/// <param name="Value">Opaque renderer-owned identifier.</param>
public readonly record struct MeshHandle(ulong Value)
{
    /// <summary>Gets whether this handle identifies a resource.</summary>
    public bool IsValid => Value != 0;
}

/// <summary>Identifies a renderer-owned joint palette.</summary>
/// <param name="Value">Opaque renderer-owned identifier.</param>
public readonly record struct SkinPaletteHandle(ulong Value)
{
    /// <summary>Gets whether this handle identifies a resource.</summary>
    public bool IsValid => Value != 0;
}

/// <summary>Identifies a renderer-owned view and render target.</summary>
/// <param name="Value">Opaque renderer-owned identifier.</param>
public readonly record struct RenderViewHandle(ulong Value)
{
    /// <summary>Gets whether this handle identifies a view.</summary>
    public bool IsValid => Value != 0;
}

/// <summary>Describes how frequently a GPU resource is expected to change.</summary>
public enum ResourceUsage
{
    /// <summary>Uploaded rarely and retained in device-local storage.</summary>
    Persistent,

    /// <summary>Updated selectively while retaining a stable resource identity.</summary>
    Dynamic,

    /// <summary>Valid only for the frame in which it is submitted.</summary>
    Transient
}

/// <summary>Describes mesh data used to create a renderer resource.</summary>
/// <param name="Vertices">Initial colored vertices.</param>
/// <param name="Usage">Expected update frequency.</param>
public sealed record MeshDescription(Vertex[] Vertices, ResourceUsage Usage = ResourceUsage.Persistent);

/// <summary>Describes a contiguous replacement within a mesh resource.</summary>
/// <param name="FirstVertex">First destination vertex.</param>
/// <param name="Vertices">Replacement vertices.</param>
public sealed record MeshUpdate(uint FirstVertex, Vertex[] Vertices);

/// <summary>Describes colored geometry valid only for the current frame.</summary>
/// <param name="Vertices">One-frame vertices.</param>
public readonly record struct TransientGeometry(Vertex[] Vertices);
