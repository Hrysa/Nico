using System.Runtime.InteropServices;

namespace Engine.Graphics;

/// <summary>Classifies one submitted surface for SRP queue filtering and sorting.</summary>
public enum RenderSurfaceType
{
    /// <summary>Solid surface rendered with depth writes.</summary>
    Opaque,
    /// <summary>Cutout surface rendered with depth writes.</summary>
    AlphaTest,
    /// <summary>Blended surface rendered after solid geometry.</summary>
    Transparent,
    /// <summary>Non-world overlay geometry.</summary>
    Overlay
}

/// <summary>
/// Describes one backend-independent mesh submission.
/// </summary>
/// <param name="Mesh">Registered geometry to render.</param>
/// <param name="PushConstants">Object and camera transforms.</param>
/// <param name="SkinPalette">Optional joint palette for a skinned mesh.</param>
/// <param name="CastsShadows">Whether this draw participates in shadow passes.</param>
/// <param name="SurfaceType">Surface class used by SRP queue filters.</param>
public readonly record struct RenderCommand(
    MeshHandle Mesh,
    PushConstants PushConstants,
    SkinPaletteHandle SkinPalette = default,
    bool CastsShadows = true,
    RenderSurfaceType SurfaceType = RenderSurfaceType.Opaque);

/// <summary>
/// Collects ordered render commands for one viewport and one frame.
/// </summary>
public sealed class RenderQueue
{
    private readonly List<RenderCommand> _commands = [];
    private readonly List<RenderPipelineCommand> _pipelineCommands = [];
    private readonly List<RenderPipelineBarrier> _pipelineBarriers = [];

    /// <summary>Gets the reusable per-view collection of enabled scene lights.</summary>
    public SceneLightSet Lights { get; } = new();

    /// <summary>Gets or sets explicit camera state consumed by view-dependent passes.</summary>
    public RenderCameraData Camera { get; set; }

    /// <summary>Gets or sets presentation effects applied to this rendered view.</summary>
    public RenderOutputSettings Output { get; internal set; } = RenderOutputSettings.None;

    /// <summary>Gets the ordered commands in this queue.</summary>
    public IReadOnlyList<RenderCommand> Commands => _commands;

    /// <summary>Gets an allocation-free view of the ordered commands for immediate enumeration.</summary>
    public ReadOnlySpan<RenderCommand> CommandSpan => CollectionsMarshal.AsSpan(_commands);

    /// <summary>Gets an allocation-free view of GPU work authored by the active SRP.</summary>
    public ReadOnlySpan<RenderPipelineCommand> PipelineCommandSpan =>
        CollectionsMarshal.AsSpan(_pipelineCommands);

    /// <summary>Gets compiled resource dependencies between SRP commands.</summary>
    public ReadOnlySpan<RenderPipelineBarrier> PipelineBarrierSpan =>
        CollectionsMarshal.AsSpan(_pipelineBarriers);

    /// <summary>Gets whether the active SRP scheduled any scene-geometry command.</summary>
    internal bool HasDrawPipelineCommand
    {
        get
        {
            for (var index = 0; index < _pipelineCommands.Count; index++)
            {
                if (_pipelineCommands[index].Kind == RenderPipelineCommandKind.DrawRenderers)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Adds geometry to the queue.</summary>
    /// <param name="mesh">Registered geometry to render.</param>
    /// <param name="pushConstants">Object and camera transforms.</param>
    /// <param name="castsShadows">Whether the draw participates in shadow passes.</param>
    /// <param name="surfaceType">Surface class used by SRP queue filters.</param>
    public void Add(
        MeshHandle mesh,
        PushConstants pushConstants,
        bool castsShadows = true,
        RenderSurfaceType surfaceType = RenderSurfaceType.Opaque)
    {
        if (!mesh.IsValid)
            throw new ArgumentException("A valid mesh handle is required.", nameof(mesh));
        _commands.Add(new RenderCommand(
            mesh, pushConstants, CastsShadows: castsShadows, SurfaceType: surfaceType));
    }

    /// <summary>Adds skinned geometry to the queue.</summary>
    /// <param name="mesh">Registered skinned geometry.</param>
    /// <param name="palette">Current joint palette for the mesh instance.</param>
    /// <param name="pushConstants">Object and camera transforms.</param>
    /// <param name="castsShadows">Whether the draw participates in shadow passes.</param>
    /// <param name="surfaceType">Surface class used by SRP queue filters.</param>
    public void AddSkinned(
        MeshHandle mesh,
        SkinPaletteHandle palette,
        PushConstants pushConstants,
        bool castsShadows = true,
        RenderSurfaceType surfaceType = RenderSurfaceType.Opaque)
    {
        if (!mesh.IsValid)
            throw new ArgumentException("A valid mesh handle is required.", nameof(mesh));
        if (!palette.IsValid)
            throw new ArgumentException("A valid skin palette is required.", nameof(palette));
        _commands.Add(new RenderCommand(
            mesh, pushConstants, palette, castsShadows, surfaceType));
    }

    /// <summary>Collects all enabled lights from a scene into this reusable queue.</summary>
    /// <param name="root">Scene hierarchy root.</param>
    public void ResolveLighting(Engine.Core.Node root)
    {
        Lights.Resolve(root);
    }

    /// <summary>Sets one directional and ambient light for a standalone preview.</summary>
    /// <param name="directionToLight">Normalized direction from surfaces toward the light.</param>
    /// <param name="color">Linear RGB light and ambient color.</param>
    /// <param name="intensity">Direct-light multiplier.</param>
    /// <param name="ambientIntensity">Ambient-light multiplier.</param>
    public void SetPreviewLighting(
        System.Numerics.Vector3 directionToLight,
        System.Numerics.Vector3 color,
        float intensity,
        float ambientIntensity)
    {
        Lights.SetDirectional(directionToLight, color, intensity, ambientIntensity);
    }

    /// <summary>Removes all commands so the queue can be reused.</summary>
    public void Clear()
    {
        _commands.Clear();
        _pipelineCommands.Clear();
        _pipelineBarriers.Clear();
        Lights.Clear();
        Camera = default;
        Output = RenderOutputSettings.None;
    }

    /// <summary>Adds one command authored by a render-pipeline pass.</summary>
    /// <param name="command">Renderer-independent GPU work description.</param>
    internal void AddPipelineCommand(RenderPipelineCommand command) =>
        _pipelineCommands.Add(command);

    /// <summary>Removes commands authored by a previous pipeline execution.</summary>
    internal void ClearPipelineCommands()
    {
        _pipelineCommands.Clear();
        _pipelineBarriers.Clear();
    }

    /// <summary>Compiles declared resource accesses into sequential dependencies.</summary>
    internal void CompilePipelineDependencies() => RenderPipelineCompiler.Compile(
        PipelineCommandSpan, _pipelineBarriers);

    /// <summary>Stably sorts transparent slots from far to near without allocating.</summary>
    internal void SortTransparentBackToFront()
    {
        if (!Camera.IsValid || !System.Numerics.Matrix4x4.Invert(
            Camera.View, out var inverseView))
        {
            return;
        }
        var cameraPosition = inverseView.Translation;
        for (var index = 1; index < _commands.Count; index++)
        {
            var key = _commands[index];
            if (key.SurfaceType != RenderSurfaceType.Transparent)
                continue;
            var keyDistance = System.Numerics.Vector3.DistanceSquared(
                key.PushConstants.Model.Translation, cameraPosition);
            var destination = index;
            for (var search = index - 1; search >= 0; search--)
            {
                var previous = _commands[search];
                if (previous.SurfaceType != RenderSurfaceType.Transparent)
                    continue;
                var previousDistance = System.Numerics.Vector3.DistanceSquared(
                    previous.PushConstants.Model.Translation, cameraPosition);
                if (previousDistance >= keyDistance)
                    break;
                _commands[destination] = previous;
                destination = search;
            }
            _commands[destination] = key;
        }
    }
}
