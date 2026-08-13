using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Identifies the height operation performed by a terrain brush.</summary>
public enum TerrainBrushMode
{
    /// <summary>Adds height under the brush.</summary>
    Raise,

    /// <summary>Removes height under the brush.</summary>
    Lower,

    /// <summary>Moves samples toward their immediate-neighbor average.</summary>
    Smooth,

    /// <summary>Moves samples toward the height captured at stroke start.</summary>
    Flatten
}

/// <summary>Describes the inclusive sample rectangle changed by one terrain dab.</summary>
/// <param name="MinimumX">First changed sample column.</param>
/// <param name="MinimumZ">First changed sample row.</param>
/// <param name="MaximumX">Last changed sample column.</param>
/// <param name="MaximumZ">Last changed sample row.</param>
public readonly record struct TerrainEditRegion(
    int MinimumX,
    int MinimumZ,
    int MaximumX,
    int MaximumZ);

/// <summary>Stores shared Scene terrain-tool settings independently of Inspector lifetime.</summary>
public sealed class TerrainBrushSettings
{
    /// <summary>Gets or sets whether primary pointer input sculpts selected terrain.</summary>
    public bool IsEnabled
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets or sets the active brush operation.</summary>
    public TerrainBrushMode Mode
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    } = TerrainBrushMode.Raise;

    /// <summary>Gets or sets brush radius in world units.</summary>
    public float Radius
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    } = 1.5f;

    /// <summary>Gets or sets world-height influence applied by each brush dab.</summary>
    public float Strength
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    } = 0.2f;

    /// <summary>Occurs when any shared tool option changes.</summary>
    public event Action? Changed;
}

/// <summary>Loads editable terrain documents from Nico terrain sources.</summary>
public sealed class TerrainDocumentFactory : IAssetDocumentFactory
{
    /// <inheritdoc/>
    public string ContentType => "nico/terrain";

    /// <inheritdoc/>
    public IAssetDocument Load(AssetDocumentLocation location, Action<AssetReference> saved)
    {
        using var stream = location.OpenRead();
        return new TerrainDocument(location, TerrainResource.Load(stream), saved);
    }
}

/// <summary>Owns mutable terrain samples, stroke history, and atomic persistence.</summary>
public sealed class TerrainDocument : IAssetDocument<TerrainResource>
{
    private readonly AssetDocumentLocation _location;
    private readonly Action<AssetReference> _saved;
    private readonly Stack<float[]> _undo = [];
    private readonly Stack<float[]> _redo = [];
    private float[] _heights;
    private float[] _smoothSource;
    private float[]? _strokeBefore;
    private bool _strokeChanged;
    private bool _strokeWasDirty;
    private bool _disposed;

    /// <inheritdoc/>
    public AssetReference Reference => _location.Reference;

    /// <inheritdoc/>
    public string DisplayName => _location.ResolveDisplayName();

    /// <inheritdoc/>
    public TerrainResource Value { get; private set; }

    object IAssetDocument.Value => Value;

    /// <inheritdoc/>
    public bool IsEditable => _location.IsEditable;

    /// <inheritdoc/>
    public bool IsDirty { get; private set; }

    /// <inheritdoc/>
    public Exception? LastError { get; private set; }

    /// <summary>Gets whether a completed terrain stroke can be restored.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Gets whether an undone terrain stroke can be reapplied.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Gets whether a terrain stroke transaction is currently active.</summary>
    public bool IsStrokeActive => _strokeBefore is not null;

    /// <inheritdoc/>
    public event Action? Changed;

    /// <summary>Creates one loaded terrain document.</summary>
    /// <param name="location">Current source location.</param>
    /// <param name="value">Decoded terrain value.</param>
    /// <param name="saved">Callback invoked after persistence.</param>
    public TerrainDocument(
        AssetDocumentLocation location,
        TerrainResource value,
        Action<AssetReference> saved)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        _heights = value.CopyHeights();
        _smoothSource = new float[_heights.Length];
    }

    /// <summary>Begins one undoable sculpt stroke.</summary>
    public void BeginStroke()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEditable();
        if (_strokeBefore is not null)
            throw new InvalidOperationException("A terrain stroke is already active.");
        _strokeBefore = (float[])_heights.Clone();
        _strokeChanged = false;
        _strokeWasDirty = IsDirty;
    }

    /// <summary>Applies one radial brush dab to current samples.</summary>
    /// <param name="u">Normalized brush center along X.</param>
    /// <param name="v">Normalized brush center along Z.</param>
    /// <param name="radiusU">Normalized X radius.</param>
    /// <param name="radiusV">Normalized Z radius.</param>
    /// <param name="amount">Normalized height influence.</param>
    /// <param name="mode">Requested height operation.</param>
    /// <param name="flattenHeight">Normalized target used by flatten mode.</param>
    /// <returns>The changed inclusive sample rectangle, or null when no sample changed.</returns>
    public TerrainEditRegion? ApplyBrush(
        float u,
        float v,
        float radiusU,
        float radiusV,
        float amount,
        TerrainBrushMode mode,
        float flattenHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEditable();
        if (_strokeBefore is null)
            throw new InvalidOperationException("BeginStroke must be called before sculpting.");
        if (!float.IsFinite(u) || !float.IsFinite(v) || !float.IsFinite(radiusU) ||
            !float.IsFinite(radiusV) || radiusU <= 0f || radiusV <= 0f ||
            !float.IsFinite(amount) || amount <= 0f || !float.IsFinite(flattenHeight))
            throw new ArgumentOutOfRangeException(nameof(amount));

        var minimumX = Math.Max(0, (int)MathF.Floor((u - radiusU) * (Value.Width - 1)));
        var maximumX = Math.Min(Value.Width - 1,
            (int)MathF.Ceiling((u + radiusU) * (Value.Width - 1)));
        var minimumZ = Math.Max(0, (int)MathF.Floor((v - radiusV) * (Value.Depth - 1)));
        var maximumZ = Math.Min(Value.Depth - 1,
            (int)MathF.Ceiling((v + radiusV) * (Value.Depth - 1)));
        var source = _heights;
        if (mode == TerrainBrushMode.Smooth)
        {
            Array.Copy(_heights, _smoothSource, _heights.Length);
            source = _smoothSource;
        }
        var changedMinimumX = Value.Width;
        var changedMinimumZ = Value.Depth;
        var changedMaximumX = -1;
        var changedMaximumZ = -1;
        for (var z = minimumZ; z <= maximumZ; z++)
        {
            var sampleV = z / (float)(Value.Depth - 1);
            var distanceV = (sampleV - v) / radiusV;
            for (var x = minimumX; x <= maximumX; x++)
            {
                var sampleU = x / (float)(Value.Width - 1);
                var distanceU = (sampleU - u) / radiusU;
                var distanceSquared = distanceU * distanceU + distanceV * distanceV;
                if (distanceSquared > 1f)
                    continue;
                var falloff = 1f - MathF.Sqrt(distanceSquared);
                falloff = falloff * falloff * (3f - 2f * falloff);
                var index = z * Value.Width + x;
                var previous = _heights[index];
                var next = mode switch
                {
                    TerrainBrushMode.Lower => previous - amount * falloff,
                    TerrainBrushMode.Smooth => previous +
                        (GetNeighborAverage(source, x, z) - previous) *
                        Math.Clamp(amount * falloff, 0f, 1f),
                    TerrainBrushMode.Flatten => previous + (flattenHeight - previous) *
                        Math.Clamp(amount * falloff, 0f, 1f),
                    _ => previous + amount * falloff
                };
                next = Math.Clamp(next, 0f, 1f);
                if (MathF.Abs(next - previous) <= 0.000001f)
                    continue;
                _heights[index] = next;
                changedMinimumX = Math.Min(changedMinimumX, x);
                changedMinimumZ = Math.Min(changedMinimumZ, z);
                changedMaximumX = Math.Max(changedMaximumX, x);
                changedMaximumZ = Math.Max(changedMaximumZ, z);
            }
        }
        if (changedMaximumX < 0)
            return null;
        Value.UpdateHeights(_heights);
        _strokeChanged = true;
        IsDirty = true;
        LastError = null;
        return new TerrainEditRegion(
            changedMinimumX, changedMinimumZ, changedMaximumX, changedMaximumZ);
    }

    /// <summary>Completes an active sculpt stroke and optionally persists it.</summary>
    /// <param name="save">Whether changed samples should be saved immediately.</param>
    /// <returns>True when the stroke changed at least one sample.</returns>
    public bool EndStroke(bool save = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_strokeBefore is null)
            return false;
        var changed = _strokeChanged;
        if (changed)
        {
            _undo.Push(_strokeBefore);
            _redo.Clear();
        }
        _strokeBefore = null;
        _strokeChanged = false;
        _strokeWasDirty = false;
        if (changed && save)
            Save();
        else
            Changed?.Invoke();
        return changed;
    }

    /// <summary>Restores samples captured before the active stroke.</summary>
    /// <returns>True when an active changed stroke was cancelled.</returns>
    public bool CancelStroke()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_strokeBefore is null)
            return false;
        var changed = _strokeChanged;
        if (changed)
        {
            _heights = _strokeBefore;
            Value.UpdateHeights(_heights);
        }
        _strokeBefore = null;
        _strokeChanged = false;
        IsDirty = _strokeWasDirty;
        _strokeWasDirty = false;
        Changed?.Invoke();
        return changed;
    }

    /// <summary>Restores the terrain state before the latest completed stroke.</summary>
    /// <returns>True when one history entry was restored.</returns>
    public bool Undo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEditable();
        EnsureNoActiveStroke();
        if (!_undo.TryPop(out var previous))
            return false;
        _redo.Push((float[])_heights.Clone());
        ReplaceSamples(previous);
        return true;
    }

    /// <summary>Reapplies the latest undone terrain stroke.</summary>
    /// <returns>True when one history entry was reapplied.</returns>
    public bool Redo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEditable();
        EnsureNoActiveStroke();
        if (!_redo.TryPop(out var next))
            return false;
        _undo.Push((float[])_heights.Clone());
        ReplaceSamples(next);
        return true;
    }

    /// <inheritdoc/>
    public void MarkDirty()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEditable();
        IsDirty = true;
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEditable();
        EnsureNoActiveStroke();
        try
        {
            _location.Write(Value.Save);
            IsDirty = false;
            LastError = null;
        }
        catch (Exception exception)
        {
            LastError = exception;
            Changed?.Invoke();
            return;
        }
        Changed?.Invoke();
        try
        {
            _saved(Reference);
        }
        catch (Exception exception)
        {
            LastError = exception;
            Changed?.Invoke();
        }
    }

    /// <inheritdoc/>
    public void Reload()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNoActiveStroke();
        var loaded = false;
        try
        {
            using var stream = _location.OpenRead();
            Value = TerrainResource.Load(stream);
            _heights = Value.CopyHeights();
            _smoothSource = new float[_heights.Length];
            _undo.Clear();
            _redo.Clear();
            IsDirty = false;
            LastError = null;
            loaded = true;
        }
        catch (Exception exception)
        {
            LastError = exception;
        }
        Changed?.Invoke();
        if (!loaded)
            return;
        try
        {
            _saved(Reference);
        }
        catch (Exception exception)
        {
            LastError = exception;
            Changed?.Invoke();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Changed = null;
        _undo.Clear();
        _redo.Clear();
        _strokeBefore = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Computes one clamped four-neighbor sample average.</summary>
    /// <param name="source">Stable source samples for the current dab.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="z">Sample row.</param>
    /// <returns>Average including the center sample.</returns>
    private float GetNeighborAverage(float[] source, int x, int z)
    {
        var sum = source[z * Value.Width + x];
        var count = 1;
        if (x > 0)
        {
            sum += source[z * Value.Width + x - 1];
            count++;
        }
        if (x + 1 < Value.Width)
        {
            sum += source[z * Value.Width + x + 1];
            count++;
        }
        if (z > 0)
        {
            sum += source[(z - 1) * Value.Width + x];
            count++;
        }
        if (z + 1 < Value.Depth)
        {
            sum += source[(z + 1) * Value.Width + x];
            count++;
        }
        return sum / count;
    }

    /// <summary>Replaces all samples from a history entry and publishes the change.</summary>
    /// <param name="samples">Owned replacement samples.</param>
    private void ReplaceSamples(float[] samples)
    {
        _heights = samples;
        Value.UpdateHeights(_heights);
        IsDirty = true;
        LastError = null;
        Changed?.Invoke();
    }

    /// <summary>Rejects mutation of imported read-only terrain outputs.</summary>
    private void EnsureEditable()
    {
        if (!IsEditable)
            throw new InvalidOperationException("This imported terrain is read-only.");
    }

    /// <summary>Rejects history and persistence operations during a stroke.</summary>
    private void EnsureNoActiveStroke()
    {
        if (_strokeBefore is not null)
            throw new InvalidOperationException("Complete or cancel the active terrain stroke first.");
    }
}

/// <summary>Creates flat editable Nico terrain sources.</summary>
public static class TerrainAuthoring
{
    /// <summary>Writes one flat terrain source with validated dimensions and initial height.</summary>
    /// <param name="path">Destination source path.</param>
    /// <param name="width">Sample columns.</param>
    /// <param name="depth">Sample rows.</param>
    /// <param name="height">Initial normalized height.</param>
    public static void SaveFlat(string path, int width = 65, int depth = 65, float height = 0.2f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!float.IsFinite(height) || height < 0f || height > 1f)
            throw new ArgumentOutOfRangeException(nameof(height));
        var heights = new float[checked(width * depth)];
        Array.Fill(heights, height);
        AssetDocumentStorage.WriteAtomic(path,
            stream => new TerrainResource(width, depth, heights).Save(stream));
    }
}
