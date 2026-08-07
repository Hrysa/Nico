// Portions derived from SixLabors.Fonts and Avalonia under the Apache License 2.0.
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Engine.Graphics.Bidi;

/// <summary>Provides a reusable growable array for bidi working storage.</summary>
/// <typeparam name="T">Element type.</typeparam>
internal struct BidiArrayBuilder<T>
{
    private T[]? _items;
    private int _length;

    /// <summary>Gets the current item count.</summary>
    internal int Length
    {
        get => _length;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            EnsureCapacity(value);
            _length = value;
        }
    }

    /// <summary>Gets a reference to an item.</summary>
    /// <param name="index">Zero-based item index.</param>
    /// <returns>A reference to the item.</returns>
    internal ref T this[int index] => ref _items![index];

    /// <summary>Adds uninitialized or cleared storage.</summary>
    /// <param name="length">Number of items to add.</param>
    /// <param name="clear">Whether the added storage should be cleared.</param>
    /// <returns>The added slice.</returns>
    internal BidiArraySlice<T> Add(int length, bool clear = true)
    {
        var start = _length;
        EnsureCapacity(checked(_length + length));
        _length += length;
        var slice = new BidiArraySlice<T>(_items!, start, length);
        if (clear)
            slice.Span.Clear();
        return slice;
    }

    /// <summary>Copies a slice to the end of the builder.</summary>
    /// <param name="source">Source slice.</param>
    /// <returns>The added slice.</returns>
    internal BidiArraySlice<T> Add(BidiArraySlice<T> source)
    {
        var result = Add(source.Length, false);
        source.Span.CopyTo(result.Span);
        return result;
    }

    /// <summary>Adds one item.</summary>
    /// <param name="item">Item to add.</param>
    internal void AddItem(T item)
    {
        EnsureCapacity(_length + 1);
        _items![_length++] = item;
    }

    /// <summary>Gets a slice over all current items.</summary>
    /// <returns>The current slice.</returns>
    internal BidiArraySlice<T> AsSlice() => new(_items ?? [], 0, _length);

    /// <summary>Gets a slice over the requested range.</summary>
    /// <param name="start">Start index.</param>
    /// <param name="length">Item count.</param>
    /// <returns>The requested slice.</returns>
    internal BidiArraySlice<T> AsSlice(int start, int length) =>
        new(_items ?? [], start, length);

    /// <summary>Clears items while retaining bounded storage.</summary>
    internal void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && _length > 0)
            Array.Clear(_items!, 0, _length);
        _length = 0;
        if (_items is { LongLength: > 1048576 })
            _items = null;
    }

    /// <summary>Ensures the backing array can hold a requested count.</summary>
    /// <param name="minimum">Required capacity.</param>
    private void EnsureCapacity(int minimum)
    {
        if ((_items?.Length ?? 0) >= minimum)
            return;
        var capacity = Math.Max(minimum, Math.Max(4, (_items?.Length ?? 0) * 2));
        Array.Resize(ref _items, capacity);
    }
}

/// <summary>Provides a mutable view over a contiguous array range.</summary>
/// <typeparam name="T">Element type.</typeparam>
internal readonly struct BidiArraySlice<T>
{
    private readonly T[] _items;

    /// <summary>Initializes a slice over a complete array.</summary>
    /// <param name="items">Backing array.</param>
    internal BidiArraySlice(T[] items) : this(items, 0, items.Length) { }

    /// <summary>Initializes a slice over an array range.</summary>
    /// <param name="items">Backing array.</param>
    /// <param name="start">Start index.</param>
    /// <param name="length">Item count.</param>
    internal BidiArraySlice(T[] items, int start, int length)
    {
        _items = items;
        Start = start;
        Length = length;
    }

    /// <summary>Gets the backing-array start index.</summary>
    internal int Start { get; }

    /// <summary>Gets the item count.</summary>
    internal int Length { get; }

    /// <summary>Gets whether the slice contains no items.</summary>
    internal bool IsEmpty => Length == 0;

    /// <summary>Gets a span over the slice.</summary>
    internal Span<T> Span => _items.AsSpan(Start, Length);

    /// <summary>Gets a reference to an item.</summary>
    /// <param name="index">Slice-relative item index.</param>
    /// <returns>A reference to the item.</returns>
    internal ref T this[int index] => ref _items[Start + index];

    /// <summary>Gets a nested slice.</summary>
    /// <param name="start">Slice-relative start index.</param>
    /// <param name="length">Item count.</param>
    /// <returns>The nested slice.</returns>
    internal BidiArraySlice<T> Slice(int start, int length) =>
        new(_items, Start + start, length);

    /// <summary>Fills all items with one value.</summary>
    /// <param name="value">Fill value.</param>
    internal void Fill(T value) => Span.Fill(value);

    /// <summary>Converts an array into a full slice.</summary>
    /// <param name="items">Source array.</param>
    public static implicit operator BidiArraySlice<T>(T[] items) => new(items);
}

/// <summary>Provides a mapped mutable view over another slice.</summary>
/// <typeparam name="T">Element type.</typeparam>
internal readonly struct MappedBidiArraySlice<T> where T : struct
{
    private readonly BidiArraySlice<T> _items;
    private readonly BidiArraySlice<int> _map;

    /// <summary>Initializes the mapped view.</summary>
    /// <param name="items">Source items.</param>
    /// <param name="map">Source indices.</param>
    internal MappedBidiArraySlice(BidiArraySlice<T> items, BidiArraySlice<int> map)
    {
        _items = items;
        _map = map;
    }

    /// <summary>Gets the mapped item count.</summary>
    internal int Length => _map.Length;

    /// <summary>Gets a reference to a mapped item.</summary>
    /// <param name="index">Mapped index.</param>
    /// <returns>A reference to the source item.</returns>
    internal ref T this[int index] => ref _items[_map[index]];
}

/// <summary>Stores one-to-one mappings in both directions.</summary>
/// <typeparam name="TKey">Forward key type.</typeparam>
/// <typeparam name="TValue">Forward value type.</typeparam>
internal sealed class BidiDictionary<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    private readonly Dictionary<TKey, TValue> _forward = [];
    private readonly Dictionary<TValue, TKey> _reverse = [];

    /// <summary>Adds a mapping.</summary>
    /// <param name="key">Forward key.</param>
    /// <param name="value">Forward value.</param>
    internal void Add(TKey key, TValue value)
    {
        _forward.Add(key, value);
        _reverse.Add(value, key);
    }

    /// <summary>Finds a forward value.</summary>
    /// <param name="key">Forward key.</param>
    /// <param name="value">Mapped value.</param>
    /// <returns>True when found.</returns>
    internal bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        _forward.TryGetValue(key, out value);

    /// <summary>Finds a reverse key.</summary>
    /// <param name="value">Forward value.</param>
    /// <param name="key">Mapped key.</param>
    /// <returns>True when found.</returns>
    internal bool TryGetKey(TValue value, [MaybeNullWhen(false)] out TKey key) =>
        _reverse.TryGetValue(value, out key);

    /// <summary>Clears both maps while retaining bounded storage.</summary>
    internal void ClearThenResetIfTooLarge()
    {
        _forward.Clear();
        _reverse.Clear();
        if (_forward.EnsureCapacity(0) > 131072)
            _forward.TrimExcess();
        if (_reverse.EnsureCapacity(0) > 131072)
            _reverse.TrimExcess();
    }
}
