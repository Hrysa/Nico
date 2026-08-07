using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Hosts persistent editor content in an independent shared-device native window.</summary>
public sealed class DetachedToolWindow : IDisposable
{
    private readonly SilkWindowGroup _windowGroup;
    private readonly Grid _root;
    private readonly UIHost _uiHost;
    private bool _disposed;

    /// <summary>Gets the native window and renderer for detached content.</summary>
    public SilkWindow Window { get; }

    /// <summary>Gets the detached content.</summary>
    public UIElement Content { get; }

    /// <summary>Gets the custom title bar shared with the main Editor window style.</summary>
    public TitleBar TitleBar { get; }

    /// <summary>Gets the UI host and its per-window input state.</summary>
    public UIHost UIHost => _uiHost;

    /// <summary>Creates a detached editor tool window.</summary>
    /// <param name="windowGroup">Shared-device window group.</param>
    /// <param name="title">Native window title.</param>
    /// <param name="width">Initial logical width.</param>
    /// <param name="height">Initial logical height.</param>
    /// <param name="content">Content moved out of the main dock.</param>
    public DetachedToolWindow(
        SilkWindowGroup windowGroup,
        string title,
        int width,
        int height,
        UIElement content)
    {
        ArgumentNullException.ThrowIfNull(windowGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(content);
        _windowGroup = windowGroup;
        Content = content;
        var theme = UITheme.Dark;
        var titleBarHeight = OperatingSystem.IsWindows() ? 36f : 48f;
        _root = new Grid(theme.Canvas);
        _root.Rows.Add(GridLength.Pixels(titleBarHeight));
        _root.Rows.Add(GridLength.Star());
        _root.Columns.Add(GridLength.Star());
        TitleBar = new TitleBar(width, titleBarHeight, theme)
        {
            Width = 0f,
            Margin = new Thickness(0f, 0f, 0f, 1f)
        };
        TitleBar.CenterZone.AddChild(new Label(title)
        {
            FontSize = theme.FontSize,
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        });
        _root.Add(TitleBar, 0, 0);
        _root.Add(content, 1, 0);
        Window = windowGroup.CreateWindow(new WindowOptions
        {
            Title = title,
            Width = width,
            Height = height,
            CustomTitleBar = true
        });
        _uiHost = new UIHost(
            Window, Window, Window, _root, width, height, textLayout: Window);
        TitleBar.DragStarted += () => Window.BeginWindowDrag(_uiHost.PointerPosition);
        TitleBar.MinimizeRequested += Window.Minimize;
        TitleBar.MaximizeRequested += Window.ToggleMaximize;
        TitleBar.FullScreenRequested += Window.ToggleFullScreen;
        TitleBar.CloseRequested += Window.Close;
        _uiHost.PointerMoveProcessed = (pointerEvent, _) =>
            Window.UpdateWindowDrag(pointerEvent.Position);
        _uiHost.PointerButtonProcessed = (pointerEvent, _) =>
        {
            if (!pointerEvent.IsPressed)
                Window.EndWindowDrag();
        };
    }

    /// <summary>Gets whether the native tool window remains open.</summary>
    public bool IsOpen => !_disposed && Window.IsRunning;

    /// <summary>Removes and returns content so it can be docked again.</summary>
    /// <returns>The hosted content.</returns>
    public UIElement ReleaseContent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _root.Remove(Content);
        return Content;
    }

    /// <summary>Releases the independent UI host and native presentation window.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _root.Remove(Content);
        _uiHost.Dispose();
        _windowGroup.DestroyWindow(Window);
        GC.SuppressFinalize(this);
    }
}
