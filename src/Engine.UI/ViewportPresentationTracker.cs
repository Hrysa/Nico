using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Keeps a render-view presentation quad aligned with its retained viewport element.</summary>
public sealed class ViewportPresentationTracker
{
    private readonly ViewportPanel _viewport;
    private readonly VertexT[] _quad = new VertexT[6];
    private IRenderer? _renderer;
    private RenderViewHandle _renderView;
    private UIClipRect _bounds;
    private bool _visible;
    private bool _synchronized;

    /// <summary>Creates a presentation tracker for one viewport element.</summary>
    /// <param name="viewport">Viewport whose absolute arranged bounds drive presentation.</param>
    public ViewportPresentationTracker(ViewportPanel viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        _viewport = viewport;
    }

    /// <summary>Updates presentation geometry only when ownership, bounds, or visibility changed.</summary>
    /// <param name="renderer">Renderer that currently owns the viewport's render view.</param>
    /// <returns>True when new presentation geometry was submitted.</returns>
    public bool Synchronize(IRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        var renderView = _viewport.RenderView;
        if (!renderView.IsValid)
        {
            Reset();
            return false;
        }

        var visible = _viewport.IsEffectivelyVisible;
        var bounds = new UIClipRect(
            _viewport.Left, _viewport.Top, _viewport.Right, _viewport.Bottom);
        if (_synchronized && ReferenceEquals(_renderer, renderer) &&
            _renderView == renderView && _bounds == bounds && _visible == visible)
            return false;

        UpdateQuad(bounds, visible);
        renderer.SetViewportQuadVertices(renderView, _quad);
        _renderer = renderer;
        _renderView = renderView;
        _bounds = bounds;
        _visible = visible;
        _synchronized = true;
        return true;
    }

    /// <summary>Discards cached renderer ownership after a render view is destroyed or transferred.</summary>
    public void Reset()
    {
        _renderer = null;
        _renderView = default;
        _bounds = default;
        _visible = false;
        _synchronized = false;
    }

    /// <summary>Updates the reusable six-vertex presentation quad without per-layout allocation.</summary>
    /// <param name="bounds">Absolute arranged viewport bounds.</param>
    /// <param name="visible">Whether presentation should have visible opacity.</param>
    private void UpdateQuad(UIClipRect bounds, bool visible)
    {
        var opacity = visible ? 1f : 0f;
        _quad[0] = new VertexT(
            new Vector3(bounds.Left, bounds.Top, 0f), new Vector2(0f, 0f), opacity);
        _quad[1] = new VertexT(
            new Vector3(bounds.Left, bounds.Bottom, 0f), new Vector2(0f, 1f), opacity);
        _quad[2] = new VertexT(
            new Vector3(bounds.Right, bounds.Bottom, 0f), new Vector2(1f, 1f), opacity);
        _quad[3] = new VertexT(
            new Vector3(bounds.Right, bounds.Bottom, 0f), new Vector2(1f, 1f), opacity);
        _quad[4] = new VertexT(
            new Vector3(bounds.Right, bounds.Top, 0f), new Vector2(1f, 0f), opacity);
        _quad[5] = new VertexT(
            new Vector3(bounds.Left, bounds.Top, 0f), new Vector2(0f, 0f), opacity);
    }
}
