using System.Runtime.InteropServices;

namespace Engine.Graphics;

/// <summary>
/// Describes one backend-independent mesh submission.
/// </summary>
/// <param name="Mesh">Registered geometry to render.</param>
/// <param name="PushConstants">Object and camera transforms.</param>
/// <param name="SkinPalette">Optional joint palette for a skinned mesh.</param>
public readonly record struct RenderCommand(
    MeshHandle Mesh,
    PushConstants PushConstants,
    SkinPaletteHandle SkinPalette = default);

/// <summary>
/// Collects ordered render commands for one viewport and one frame.
/// </summary>
public sealed class RenderQueue
{
    private readonly List<RenderCommand> _commands = [];

    /// <summary>Gets or sets lighting applied to model submissions in this queue.</summary>
    public SceneLighting Lighting { get; set; } = SceneLighting.None;

    /// <summary>Gets or sets presentation effects applied to this rendered view.</summary>
    public RenderOutputSettings Output { get; set; } = RenderOutputSettings.None;

    /// <summary>Gets the ordered commands in this queue.</summary>
    public IReadOnlyList<RenderCommand> Commands => _commands;

    /// <summary>Gets an allocation-free view of the ordered commands for immediate enumeration.</summary>
    public ReadOnlySpan<RenderCommand> CommandSpan => CollectionsMarshal.AsSpan(_commands);

    /// <summary>Adds geometry to the queue.</summary>
    /// <param name="mesh">Registered geometry to render.</param>
    /// <param name="pushConstants">Object and camera transforms.</param>
    public void Add(MeshHandle mesh, PushConstants pushConstants)
    {
        if (!mesh.IsValid)
            throw new ArgumentException("A valid mesh handle is required.", nameof(mesh));
        _commands.Add(new RenderCommand(mesh, pushConstants));
    }

    /// <summary>Adds skinned geometry to the queue.</summary>
    /// <param name="mesh">Registered skinned geometry.</param>
    /// <param name="palette">Current joint palette for the mesh instance.</param>
    /// <param name="pushConstants">Object and camera transforms.</param>
    public void AddSkinned(
        MeshHandle mesh,
        SkinPaletteHandle palette,
        PushConstants pushConstants)
    {
        if (!mesh.IsValid)
            throw new ArgumentException("A valid mesh handle is required.", nameof(mesh));
        if (!palette.IsValid)
            throw new ArgumentException("A valid skin palette is required.", nameof(palette));
        _commands.Add(new RenderCommand(mesh, pushConstants, palette));
    }

    /// <summary>Removes all commands so the queue can be reused.</summary>
    public void Clear()
    {
        _commands.Clear();
        Output = RenderOutputSettings.None;
    }
}
