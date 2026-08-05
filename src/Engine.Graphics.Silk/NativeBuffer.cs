using System.Runtime.InteropServices;

namespace Engine.Graphics;

/// <summary>Owns a reusable, growable block of unmanaged values.</summary>
/// <typeparam name="T">Unmanaged value type stored in the buffer.</typeparam>
internal unsafe sealed class NativeBuffer<T> : IDisposable where T : unmanaged
{
    private T* _data;
    private int _count;
    private int _capacity;
    private bool _disposed;

    /// <summary>Gets the number of initialized values.</summary>
    public int Count => _count;

    /// <summary>Gets the initialized portion of the buffer.</summary>
    public ReadOnlySpan<T> WrittenSpan => new(_data, _count);

    /// <summary>Removes all values without releasing the backing allocation.</summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _count = 0;
    }

    /// <summary>Appends one value to the buffer.</summary>
    /// <param name="value">Value to append.</param>
    public void Add(T value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(checked(_count + 1));
        _data[_count++] = value;
    }

    /// <summary>Replaces the initialized values with a copy of the source span.</summary>
    /// <param name="source">Values to copy.</param>
    public void ReplaceWith(ReadOnlySpan<T> source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(source.Length);
        source.CopyTo(new Span<T>(_data, source.Length));
        _count = source.Length;
    }

    /// <summary>Ensures the backing allocation can hold the requested value count.</summary>
    /// <param name="requiredCapacity">Minimum required capacity.</param>
    public void EnsureCapacity(int requiredCapacity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (requiredCapacity <= _capacity)
            return;

        var grownCapacity = _capacity == 0 ? 1024 : checked(_capacity * 2);
        var newCapacity = Math.Max(requiredCapacity, grownCapacity);
        var byteCount = checked((nuint)newCapacity * (nuint)sizeof(T));
        _data = (T*)(_data is null
            ? NativeMemory.Alloc(byteCount)
            : NativeMemory.Realloc(_data, byteCount));
        _capacity = newCapacity;
    }

    /// <summary>Releases the unmanaged backing allocation.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        NativeMemory.Free(_data);
        _data = null;
        _count = 0;
        _capacity = 0;
    }
}
