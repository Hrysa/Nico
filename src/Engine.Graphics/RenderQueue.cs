namespace Engine.Graphics;

/// <summary>
/// Describes one backend-independent mesh submission.
/// </summary>
/// <param name="Mesh">Registered geometry to render.</param>
/// <param name="PushConstants">Object and camera transforms.</param>
public readonly record struct RenderCommand(MeshHandle Mesh, PushConstants PushConstants);

/// <summary>
/// Collects ordered render commands for one viewport and one frame.
/// </summary>
public sealed class RenderQueue
{
    private readonly List<RenderCommand> _commands = [];

    /// <summary>Gets the ordered commands in this queue.</summary>
    public IReadOnlyList<RenderCommand> Commands => _commands;

    /// <summary>Adds geometry to the queue.</summary>
    /// <param name="mesh">Registered geometry to render.</param>
    /// <param name="pushConstants">Object and camera transforms.</param>
    public void Add(MeshHandle mesh, PushConstants pushConstants)
    {
        if (!mesh.IsValid)
            throw new ArgumentException("A valid mesh handle is required.", nameof(mesh));
        _commands.Add(new RenderCommand(mesh, pushConstants));
    }

    /// <summary>Removes all commands so the queue can be reused.</summary>
    public void Clear()
    {
        _commands.Clear();
    }
}
