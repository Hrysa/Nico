using Microsoft.Extensions.Logging;

namespace Engine.Graphics;

/// <summary>Owns secondary native windows that share a primary window's Vulkan device.</summary>
public sealed class SilkWindowGroup : IDisposable
{
    private readonly SilkWindow _primary;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<SilkWindow> _secondaryWindows = [];
    private bool _disposed;

    /// <summary>Creates a shared-device native window group.</summary>
    /// <param name="primary">Initialized device-owning primary window.</param>
    /// <param name="loggerFactory">Logger factory passed to secondary windows.</param>
    public SilkWindowGroup(SilkWindow primary, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _primary = primary;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Gets the currently open secondary windows.</summary>
    public IReadOnlyList<SilkWindow> SecondaryWindows => _secondaryWindows;

    /// <summary>Creates and initializes a secondary window on the shared Vulkan device.</summary>
    /// <param name="options">Native window configuration.</param>
    /// <returns>The initialized secondary window.</returns>
    public SilkWindow CreateWindow(WindowOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var window = new SilkWindow(_primary, _loggerFactory);
        try
        {
            window.Initialize(options);
            _secondaryWindows.Add(window);
            return window;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    /// <summary>Pumps one frame for every open secondary window and removes closed windows.</summary>
    public void PumpFrames()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var index = _secondaryWindows.Count - 1; index >= 0; index--)
        {
            var window = _secondaryWindows[index];
            if (!window.IsRunning)
                continue;
            window.PumpFrame();
        }
    }

    /// <summary>Closes and removes one secondary window.</summary>
    /// <param name="window">Window previously created by this group.</param>
    /// <returns>True when the window belonged to this group.</returns>
    public bool DestroyWindow(SilkWindow window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        if (!_secondaryWindows.Remove(window))
            return false;
        window.Dispose();
        return true;
    }

    /// <summary>Closes and releases every secondary before the primary device is destroyed.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var index = _secondaryWindows.Count - 1; index >= 0; index--)
            _secondaryWindows[index].Dispose();
        _secondaryWindows.Clear();
        GC.SuppressFinalize(this);
    }
}
