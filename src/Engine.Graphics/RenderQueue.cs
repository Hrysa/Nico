namespace Engine.Graphics;

/// <summary>
/// Describes one backend-independent mesh submission.
/// </summary>
/// <param name="Vertices">Geometry to render.</param>
/// <param name="PushConstants">Object and camera transforms.</param>
public readonly record struct RenderCommand(Vertex[] Vertices, PushConstants PushConstants);

/// <summary>
/// Collects ordered render commands for one viewport and one frame.
/// </summary>
public sealed class RenderQueue
{
    private readonly List<RenderCommand> _commands = [];

    /// <summary>Gets the ordered commands in this queue.</summary>
    public IReadOnlyList<RenderCommand> Commands => _commands;

    /// <summary>Adds geometry to the queue.</summary>
    /// <param name="vertices">Geometry to render.</param>
    /// <param name="pushConstants">Object and camera transforms.</param>
    public void Add(Vertex[] vertices, PushConstants pushConstants)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        _commands.Add(new RenderCommand(vertices, pushConstants));
    }

    /// <summary>Removes all commands so the queue can be reused.</summary>
    public void Clear()
    {
        _commands.Clear();
    }
}
