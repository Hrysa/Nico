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

/// <summary>Describes a contiguous model-vertex replacement using reusable source storage.</summary>
public readonly record struct StaticMeshVertexUpdate
{
    /// <summary>Gets the first destination vertex.</summary>
    public uint FirstVertex { get; }

    /// <summary>Gets reusable source vertex storage.</summary>
    public ModelVertex[] Vertices { get; }

    /// <summary>Gets the first populated source vertex.</summary>
    public int SourceIndex { get; }

    /// <summary>Gets the number of vertices to replace.</summary>
    public int VertexCount { get; }

    /// <summary>Creates one validated contiguous model-vertex replacement.</summary>
    /// <param name="firstVertex">First destination vertex.</param>
    /// <param name="vertices">Reusable source storage.</param>
    /// <param name="sourceIndex">First populated source vertex.</param>
    /// <param name="vertexCount">Number of vertices to replace.</param>
    public StaticMeshVertexUpdate(
        uint firstVertex,
        ModelVertex[] vertices,
        int sourceIndex,
        int vertexCount)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (sourceIndex < 0 || vertexCount <= 0 ||
            (long)sourceIndex + vertexCount > vertices.Length)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        FirstVertex = firstVertex;
        Vertices = vertices;
        SourceIndex = sourceIndex;
        VertexCount = vertexCount;
    }
}

/// <summary>Describes colored screen-space geometry valid only for the current frame.</summary>
public readonly record struct TransientGeometry
{
    /// <summary>Creates geometry using every vertex in an exact-sized array.</summary>
    /// <param name="vertices">One-frame vertices.</param>
    /// <param name="clip">Optional logical UI bounds containing the geometry.</param>
    public TransientGeometry(Vertex[] vertices, UIClipRect? clip = null)
        : this(vertices, vertices?.Length ?? 0, clip)
    {
    }

    /// <summary>Creates geometry over the populated prefix of a reusable array.</summary>
    /// <param name="vertices">Reusable vertex storage.</param>
    /// <param name="vertexCount">Number of populated vertices at the start of the array.</param>
    /// <param name="clip">Optional logical UI bounds containing the geometry.</param>
    public TransientGeometry(Vertex[] vertices, int vertexCount, UIClipRect? clip = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if ((uint)vertexCount > (uint)vertices.Length)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        Vertices = vertices;
        VertexCount = vertexCount;
        Clip = clip;
    }

    /// <summary>Gets reusable vertex storage.</summary>
    public Vertex[] Vertices { get; }

    /// <summary>Gets the populated vertex count.</summary>
    public int VertexCount { get; }

    /// <summary>Gets optional logical UI bounds containing the geometry.</summary>
    public UIClipRect? Clip { get; }
}
