using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Routes keyboard and text input exclusively to a focused Scene viewport.</summary>
public sealed class SceneViewportInputContext : IDisposable
{
    private readonly ViewportPanel _viewport;
    private readonly FlyCameraController _flyCamera;
    private bool _disposed;

    /// <summary>Creates input ownership for one Scene viewport.</summary>
    /// <param name="viewport">Scene viewport that owns the context while focused.</param>
    /// <param name="flyCamera">Fly-camera controller receiving owned keyboard input.</param>
    public SceneViewportInputContext(ViewportPanel viewport, FlyCameraController flyCamera)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(flyCamera);
        _viewport = viewport;
        _flyCamera = flyCamera;
        _viewport.Blur += OnViewportBlur;
    }

    /// <summary>Routes a keyboard transition when the Scene viewport owns keyboard focus.</summary>
    /// <param name="router">Input router whose focus determines ownership.</param>
    /// <param name="keyEvent">Keyboard transition to route.</param>
    /// <returns>True when fly-camera mode consumed the transition.</returns>
    public bool RouteKey(UIEventRouter router, KeyInputEvent keyEvent)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (!OwnsKeyboard(router))
            return false;
        return keyEvent.IsPressed
            ? _flyCamera.KeyDown(keyEvent.Key)
            : _flyCamera.KeyUp(keyEvent.Key);
    }

    /// <summary>Checks whether active fly-camera mode consumes text or IME input.</summary>
    /// <param name="router">Input router whose focus determines ownership.</param>
    /// <returns>True only while the focused Scene viewport is in fly-camera mode.</returns>
    public bool RoutesText(UIEventRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        return OwnsKeyboard(router) && _flyCamera.IsActive;
    }

    /// <summary>Checks whether the Scene viewport contains the router's focused element.</summary>
    /// <param name="router">Input router to inspect.</param>
    /// <returns>True when focus is on the Scene viewport or one of its descendants.</returns>
    public bool OwnsKeyboard(UIEventRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        Node? current = router.FocusedElement;
        while (current is not null)
        {
            if (ReferenceEquals(current, _viewport))
                return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>Unsubscribes viewport focus handling and releases held camera input.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _viewport.Blur -= OnViewportBlur;
        _flyCamera.ReleaseFocus();
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases fly-camera state when the viewport loses keyboard focus.</summary>
    private void OnViewportBlur()
    {
        _flyCamera.ReleaseFocus();
    }
}
