using System.Numerics;

namespace Engine.UI;

/// <summary>Owns transient drag visuals for one UI host overlay.</summary>
public sealed class UIOverlayManager : IDisposable
{
    private static readonly Vector2 PreviewOffset = new(12f, 16f);
    private readonly Canvas _overlay;
    private readonly UIEventRouter _router;
    private readonly UITheme _theme;
    private DragPreview? _preview;
    private DropIndicator? _indicator;
    private bool _disposed;

    /// <summary>Gets the host-local transient notification stack.</summary>
    public ToastHost Toasts { get; }

    /// <summary>Gets the active drag preview, or null.</summary>
    public DragPreview? DragPreview => _preview;

    /// <summary>Gets the active drop indicator, or null.</summary>
    public DropIndicator? DropIndicator => _indicator;

    /// <summary>Creates an overlay manager attached to one router and overlay canvas.</summary>
    /// <param name="overlay">Host-local overlay canvas.</param>
    /// <param name="router">Host-local input router.</param>
    /// <param name="theme">Theme supplying transient visual colors.</param>
    public UIOverlayManager(Canvas overlay, UIEventRouter router, UITheme? theme = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(router);
        _overlay = overlay;
        _router = router;
        _theme = theme ?? UITheme.Dark;
        Toasts = new ToastHost(_theme);
        _overlay.Add(Toasts, Vector2.Zero);
        _router.DragStateChanged += OnDragStateChanged;
    }

    /// <summary>Advances transient overlay lifetimes for hosts not using <see cref="UIHost"/>.</summary>
    /// <param name="deltaTime">Elapsed time in seconds.</param>
    /// <returns>True when a transient visual changed.</returns>
    public bool AdvanceTime(double deltaTime) => Toasts.Advance(deltaTime);

    /// <summary>Detaches routing and removes any active transient visuals.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _router.DragStateChanged -= OnDragStateChanged;
        RemoveVisuals();
        _overlay.Remove(Toasts);
        GC.SuppressFinalize(this);
    }

    /// <summary>Synchronizes transient overlay children with router drag state.</summary>
    private void OnDragStateChanged()
    {
        if (!_router.IsDragging || _router.ActiveDragData is not { } data)
        {
            RemoveVisuals();
            return;
        }
        if (_preview is null)
        {
            _preview = new DragPreview(data.DisplayText, _theme)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            _overlay.Add(_preview, Vector2.Zero);
        }
        _overlay.SetPosition(_preview, _router.PointerPosition + PreviewOffset);

        if (_router.DropTarget is not { } target || _router.DragEffect == UIDragEffect.None)
        {
            RemoveIndicator();
            return;
        }
        if (_indicator is null)
        {
            _indicator = new DropIndicator(_theme.Accent);
            _overlay.Add(_indicator, Vector2.Zero);
        }
        _indicator.Width = target.Width;
        _indicator.Height = target.Height;
        _overlay.SetPosition(_indicator, new Vector2(target.Left, target.Top));
    }

    /// <summary>Removes both drag preview and drop indicator.</summary>
    private void RemoveVisuals()
    {
        if (_preview is not null)
        {
            _overlay.Remove(_preview);
            _preview = null;
        }
        RemoveIndicator();
    }

    /// <summary>Removes the drop indicator when no target currently accepts the drag.</summary>
    private void RemoveIndicator()
    {
        if (_indicator is null)
            return;
        _overlay.Remove(_indicator);
        _indicator = null;
    }
}
