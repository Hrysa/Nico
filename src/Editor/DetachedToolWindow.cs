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
        _root = new Grid(UITheme.Dark.Canvas);
        _root.Rows.Add(GridLength.Star());
        _root.Columns.Add(GridLength.Star());
        _root.Add(content, 0, 0);
        Window = windowGroup.CreateWindow(new WindowOptions
        {
            Title = title,
            Width = width,
            Height = height,
            CustomTitleBar = false
        });
        _uiHost = new UIHost(
            Window, Window, Window, _root, width, height, textLayout: Window);
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
