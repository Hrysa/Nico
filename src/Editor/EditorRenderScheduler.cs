namespace Editor;

/// <summary>Identifies editor render targets whose retained content is stale.</summary>
[Flags]
public enum RenderInvalidation
{
    /// <summary>No render target is stale.</summary>
    None = 0,
    /// <summary>The main editor UI must be presented again.</summary>
    UI = 1,
    /// <summary>The Scene viewport must be rebuilt.</summary>
    SceneViewport = 2,
    /// <summary>The Game viewport must be rebuilt.</summary>
    GameViewport = 4,
    /// <summary>Every editor render target is stale.</summary>
    All = UI | SceneViewport | GameViewport
}

/// <summary>Tracks event-driven invalidation independently for editor render targets.</summary>
public sealed class EditorRenderScheduler
{
    private RenderInvalidation _pending = RenderInvalidation.All;

    /// <summary>Gets the currently pending invalidation flags.</summary>
    public RenderInvalidation Pending => _pending;

    /// <summary>Marks one or more render targets stale.</summary>
    /// <param name="invalidation">Targets requiring another render.</param>
    public void Invalidate(RenderInvalidation invalidation)
    {
        _pending |= invalidation;
    }

    /// <summary>Consumes one pending target invalidation.</summary>
    /// <param name="invalidation">Single target flag to consume.</param>
    /// <returns>True when the target was pending.</returns>
    public bool Consume(RenderInvalidation invalidation)
    {
        if ((_pending & invalidation) == 0)
            return false;
        _pending &= ~invalidation;
        return true;
    }
}
