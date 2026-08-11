using Engine.Graphics;

namespace Editor;

/// <summary>Keeps the active game pipeline synchronized across docked and detached renderers.</summary>
internal sealed class EditorGameRenderingService : ISceneRenderingService
{
    private readonly EditorViewportRenderer _primary;
    private EditorViewportRenderer? _detached;

    /// <summary>Creates a service backed by the editor's primary viewport renderer.</summary>
    /// <param name="primary">Primary docked viewport renderer.</param>
    public EditorGameRenderingService(EditorViewportRenderer primary)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
    }

    /// <inheritdoc/>
    public RenderPipeline RenderPipeline
    {
        get => _primary.RenderPipeline;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _primary.RenderPipeline = value;
            if (_detached is not null)
                _detached.RenderPipeline = value;
        }
    }

    /// <summary>Changes the optional detached Game viewport renderer.</summary>
    /// <param name="renderer">Detached renderer, or null after redocking.</param>
    public void SetDetachedRenderer(EditorViewportRenderer? renderer)
    {
        _detached = renderer;
        if (_detached is not null)
            _detached.RenderPipeline = _primary.RenderPipeline;
    }
}
