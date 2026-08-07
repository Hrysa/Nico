using Microsoft.Extensions.Logging;

namespace Engine.Graphics;

/// <summary>Owns secondary native windows that share a primary window's Vulkan device.</summary>
public sealed class SilkWindowGroup : IDisposable
{
    private readonly SilkWindow _primary;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<SilkWindow> _secondaryWindows = [];
    private readonly HashSet<SilkWindow> _pendingDestruction = [];
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

    /// <summary>Updates and renders secondary windows after the primary processed global events.</summary>
    public void PumpFrames()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            for (var index = _secondaryWindows.Count - 1; index >= 0; index--)
            {
                var window = _secondaryWindows[index];
                if (_pendingDestruction.Contains(window))
                    continue;
                if (!window.IsRunning)
                {
                    _pendingDestruction.Add(window);
                    continue;
                }
                window.PumpUpdateAndRender();
            }
        }
        finally
        {
            DestroyPendingWindows();
        }
    }

    /// <summary>Closes and removes one secondary window.</summary>
    /// <param name="window">Window previously created by this group.</param>
    /// <returns>True when the window belonged to this group.</returns>
    public bool DestroyWindow(SilkWindow window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        if (!_secondaryWindows.Contains(window) || !_pendingDestruction.Add(window))
            return false;
        window.Close();
        return true;
    }

    /// <summary>Disposes windows after their active Silk.NET frame callbacks have returned.</summary>
    private void DestroyPendingWindows()
    {
        foreach (var window in _pendingDestruction)
        {
            _secondaryWindows.Remove(window);
            window.Dispose();
        }
        _pendingDestruction.Clear();
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
        _pendingDestruction.Clear();
        GC.SuppressFinalize(this);
    }
}
