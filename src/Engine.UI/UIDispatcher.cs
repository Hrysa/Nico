using System.Collections.Concurrent;

namespace Engine.UI;

/// <summary>Marshals queued work onto the thread that owns one UI host.</summary>
public sealed class UIDispatcher : IDisposable
{
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly Action _requestFrame;
    private readonly int _ownerThreadId;
    private bool _disposed;

    /// <summary>Creates a dispatcher owned by the calling thread.</summary>
    /// <param name="requestFrame">Callback that wakes or schedules the owning UI host.</param>
    public UIDispatcher(Action requestFrame)
    {
        ArgumentNullException.ThrowIfNull(requestFrame);
        _requestFrame = requestFrame;
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>Gets whether the calling thread owns this dispatcher.</summary>
    /// <returns>True when called from the owning UI thread.</returns>
    public bool CheckAccess()
    {
        return Environment.CurrentManagedThreadId == _ownerThreadId;
    }

    /// <summary>Throws when the calling thread does not own this dispatcher.</summary>
    public void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CheckAccess())
            throw new InvalidOperationException("UI state may only be accessed from its owning thread.");
    }

    /// <summary>Queues work for the owning UI thread and requests a host frame.</summary>
    /// <param name="action">Work to execute on the UI thread.</param>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pending.Enqueue(action);
        _requestFrame();
    }

    /// <summary>Requests one frame from the owning host without queuing work.</summary>
    public void RequestFrame()
    {
        VerifyAccess();
        _requestFrame();
    }

    /// <summary>Executes all work queued before or during this drain operation.</summary>
    /// <returns>Number of callbacks executed.</returns>
    public int Drain()
    {
        VerifyAccess();
        var executed = 0;
        while (_pending.TryDequeue(out var action))
        {
            action();
            executed++;
        }
        return executed;
    }

    /// <summary>Rejects future work and releases callbacks that have not executed.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        VerifyAccess();
        _disposed = true;
        while (_pending.TryDequeue(out _))
        {
        }
        GC.SuppressFinalize(this);
    }
}
