using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Chooses whether the Scene terrain brush edits height or layer weights.</summary>
public enum TerrainToolMode
{
    /// <summary>Edits terrain height samples.</summary>
    Sculpt,

    /// <summary>Paints one terrain-material layer.</summary>
    Paint,

    /// <summary>Paints persistent mesh instances onto the terrain surface.</summary>
    Objects,

    /// <summary>Locally increases or decreases active terrain sample density.</summary>
    Samples
}

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
    /// <summary>Gets or sets whether the sample-density brush increases local detail.</summary>
    public bool IncreaseSamples
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    } = true;

    /// <summary>Gets or sets whether the brush edits heights, layers, or scene objects.</summary>
    public TerrainToolMode ToolMode
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

    /// <summary>Gets or sets the selected terrain-material layer channel.</summary>
    public int PaintLayer
    {
        get;
        set
        {
            if ((uint)value >= TerrainMaterialAsset.MaximumLayers)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets or sets the static mesh painted by the object brush.</summary>
    public AssetReference? ObjectMesh
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

    /// <summary>Gets or sets whether the object brush erases matching painted instances.</summary>
    public bool EraseObjects
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

    /// <summary>Gets or sets the minimum world-space distance between painted instances.</summary>
    public float ObjectSpacing
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

    /// <summary>Gets or sets the normalized number of placement attempts per brush area.</summary>
    public float ObjectDensity
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value is <= 0f or > 1f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    } = 0.65f;

    /// <summary>Gets or sets the smallest random uniform object scale.</summary>
    public float MinimumObjectScale
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            if (MaximumObjectScale < value)
                MaximumObjectScale = value;
            Changed?.Invoke();
        }
    } = 0.85f;

    /// <summary>Gets or sets the largest random uniform object scale.</summary>
    public float MaximumObjectScale
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            if (MinimumObjectScale > value)
                MinimumObjectScale = value;
            Changed?.Invoke();
        }
    } = 1.15f;

    /// <summary>Gets or sets whether painted object up axes follow the terrain normal.</summary>
    public bool AlignObjectsToNormal
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    } = true;

    /// <summary>Gets or sets whether painted objects receive a random full yaw rotation.</summary>
    public bool RandomizeObjectYaw
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Changed?.Invoke();
        }
    } = true;

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

    /// <summary>Refreshes tool observers after related stroke history changes.</summary>
    internal void RefreshObservers()
    {
        Changed?.Invoke();
    }
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
    private readonly Stack<TerrainResource> _undo = [];
    private readonly Stack<TerrainResource> _redo = [];
    private TerrainSamplePoint[] _activeSamples = [];
    private int[] _activeSampleLookup = [];
    private float[] _smoothSource = [];
    private TerrainResource? _strokeBefore;
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
        RefreshActiveSampleCache();
    }

    /// <summary>Begins one undoable sculpt stroke.</summary>
    public void BeginStroke()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEditable();
        if (_strokeBefore is not null)
            throw new InvalidOperationException("A terrain stroke is already active.");
        _strokeBefore = Value.Clone();
        RefreshActiveSampleCache();
        _strokeChanged = false;
        _strokeWasDirty = IsDirty;
    }

    /// <summary>Applies one radial brush dab to current samples.</summary>
    /// <param name="u">Normalized brush center along X.</param>
    /// <param name="v">Normalized brush center along Z.</param>
    /// <param name="radiusU">Normalized X radius.</param>
    /// <param name="radiusV">Normalized Z radius.</param>
    /// <param name="amount">Height-sample influence.</param>
    /// <param name="mode">Requested height operation.</param>
    /// <param name="flattenHeight">Height-sample target used by flatten mode.</param>
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

        var source = _smoothSource;
        if (mode == TerrainBrushMode.Smooth)
        {
            for (var index = 0; index < _activeSamples.Length; index++)
                source[index] = Value.GetSampleHeight(_activeSamples[index]);
        }
        var changedMinimumX = Value.Width;
        var changedMinimumZ = Value.Depth;
        var changedMaximumX = -1;
        var changedMaximumZ = -1;
        for (var sampleIndex = 0; sampleIndex < _activeSamples.Length; sampleIndex++)
        {
            var sample = _activeSamples[sampleIndex];
            var sampleV = sample.FineZ / (float)(Value.FineDepth - 1);
            var distanceV = (sampleV - v) / radiusV;
            var sampleU = sample.FineX / (float)(Value.FineWidth - 1);
            var distanceU = (sampleU - u) / radiusU;
            var distanceSquared = distanceU * distanceU + distanceV * distanceV;
            if (distanceSquared > 1f)
                continue;
            var falloff = 1f - MathF.Sqrt(distanceSquared);
            falloff = falloff * falloff * (3f - 2f * falloff);
            var previous = Value.GetSampleHeight(sample);
            var next = mode switch
            {
                TerrainBrushMode.Lower => previous - amount * falloff,
                TerrainBrushMode.Smooth => previous +
                    (GetNeighborAverage(source, sample) - previous) *
                    Math.Clamp(amount * falloff, 0f, 1f),
                TerrainBrushMode.Flatten => previous + (flattenHeight - previous) *
                    Math.Clamp(amount * falloff, 0f, 1f),
                _ => previous + amount * falloff
            };
            if (!float.IsFinite(next))
            {
                throw new InvalidOperationException(
                    "Terrain sculpting produced a non-finite height sample.");
            }
            if (MathF.Abs(next - previous) <= 0.000001f)
                continue;
            Value.SetSampleHeight(sample, next);
            var minimumBaseX = sample.FineX / 2;
            var minimumBaseZ = sample.FineZ / 2;
            var maximumBaseX = Math.Min(Value.Width - 1, (sample.FineX + 1) / 2);
            var maximumBaseZ = Math.Min(Value.Depth - 1, (sample.FineZ + 1) / 2);
            changedMinimumX = Math.Min(changedMinimumX, minimumBaseX);
            changedMinimumZ = Math.Min(changedMinimumZ, minimumBaseZ);
            changedMaximumX = Math.Max(changedMaximumX, maximumBaseX);
            changedMaximumZ = Math.Max(changedMaximumZ, maximumBaseZ);
        }
        if (changedMaximumX < 0)
            return null;
        _strokeChanged = true;
        IsDirty = true;
        LastError = null;
        return new TerrainEditRegion(
            changedMinimumX, changedMinimumZ, changedMaximumX, changedMaximumZ);
    }

    /// <summary>Locally increases or decreases active terrain sample density under a brush.</summary>
    /// <param name="u">Normalized brush center along X.</param>
    /// <param name="v">Normalized brush center along Z.</param>
    /// <param name="radiusU">Normalized X radius.</param>
    /// <param name="radiusV">Normalized Z radius.</param>
    /// <param name="increase">True to refine touched quads; false to coarsen them.</param>
    /// <returns>The changed inclusive base-sample rectangle, or null.</returns>
    public TerrainEditRegion? ApplySampleDensity(
        float u,
        float v,
        float radiusU,
        float radiusV,
        bool increase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureEditable();
        if (_strokeBefore is null)
            throw new InvalidOperationException("BeginStroke must be called before resizing samples.");
        if (!float.IsFinite(u) || !float.IsFinite(v) || !float.IsFinite(radiusU) ||
            !float.IsFinite(radiusV) || radiusU <= 0f || radiusV <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radiusU));
        var minimumQuadX = Math.Max(0,
            (int)MathF.Floor((u - radiusU) * (Value.Width - 1)));
        var maximumQuadX = Math.Min(Value.Width - 2,
            (int)MathF.Floor((u + radiusU) * (Value.Width - 1)));
        var minimumQuadZ = Math.Max(0,
            (int)MathF.Floor((v - radiusV) * (Value.Depth - 1)));
        var maximumQuadZ = Math.Min(Value.Depth - 2,
            (int)MathF.Floor((v + radiusV) * (Value.Depth - 1)));
        var changedMinimumX = Value.Width;
        var changedMinimumZ = Value.Depth;
        var changedMaximumX = -1;
        var changedMaximumZ = -1;
        for (var z = minimumQuadZ; z <= maximumQuadZ; z++)
        {
            var sampleV = (z + 0.5f) / (Value.Depth - 1);
            var distanceV = (sampleV - v) / radiusV;
            for (var x = minimumQuadX; x <= maximumQuadX; x++)
            {
                var sampleU = (x + 0.5f) / (Value.Width - 1);
                var distanceU = (sampleU - u) / radiusU;
                if (distanceU * distanceU + distanceV * distanceV > 1f ||
                    !Value.SetQuadRefined(x, z, increase))
                    continue;
                changedMinimumX = Math.Min(changedMinimumX, x);
                changedMinimumZ = Math.Min(changedMinimumZ, z);
                changedMaximumX = Math.Max(changedMaximumX, x + 1);
                changedMaximumZ = Math.Max(changedMaximumZ, z + 1);
            }
        }
        if (changedMaximumX < 0)
            return null;
        RefreshActiveSampleCache();
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
            Value = _strokeBefore;
            RefreshActiveSampleCache();
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
        _redo.Push(Value.Clone());
        ReplaceResource(previous);
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
        _undo.Push(Value.Clone());
        ReplaceResource(next);
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
            RefreshActiveSampleCache();
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
    private float GetNeighborAverage(float[] source, TerrainSamplePoint sample)
    {
        var centerIndex = _activeSampleLookup[
            sample.FineZ * Value.FineWidth + sample.FineX];
        var sum = source[centerIndex];
        var count = 1;
        AddNearestNeighbor(source, sample, -1, 0, ref sum, ref count);
        AddNearestNeighbor(source, sample, 1, 0, ref sum, ref count);
        AddNearestNeighbor(source, sample, 0, -1, ref sum, ref count);
        AddNearestNeighbor(source, sample, 0, 1, ref sum, ref count);
        return sum / count;
    }

    /// <summary>Adds the nearest active sample along one lattice direction.</summary>
    /// <param name="source">Stable active height payload.</param>
    /// <param name="sample">Center sample.</param>
    /// <param name="stepX">Horizontal direction from minus one through one.</param>
    /// <param name="stepZ">Depth direction from minus one through one.</param>
    /// <param name="sum">Accumulated height sum.</param>
    /// <param name="count">Accumulated sample count.</param>
    private void AddNearestNeighbor(
        float[] source,
        TerrainSamplePoint sample,
        int stepX,
        int stepZ,
        ref float sum,
        ref int count)
    {
        for (var distance = 1; distance <= 2; distance++)
        {
            var fineX = sample.FineX + stepX * distance;
            var fineZ = sample.FineZ + stepZ * distance;
            if ((uint)fineX >= (uint)Value.FineWidth ||
                (uint)fineZ >= (uint)Value.FineDepth)
                return;
            var index = _activeSampleLookup[fineZ * Value.FineWidth + fineX];
            if (index < 0)
                continue;
            sum += source[index];
            count++;
            return;
        }
    }

    /// <summary>Replaces the terrain from a history entry and publishes the change.</summary>
    /// <param name="resource">Owned replacement terrain state.</param>
    private void ReplaceResource(TerrainResource resource)
    {
        Value = resource;
        RefreshActiveSampleCache();
        IsDirty = true;
        LastError = null;
        Changed?.Invoke();
    }

    /// <summary>Rebuilds allocation-free active-sample lookup storage after topology changes.</summary>
    private void RefreshActiveSampleCache()
    {
        _activeSamples = Value.GetActiveSamples().ToArray();
        _smoothSource = new float[_activeSamples.Length];
        _activeSampleLookup = new int[checked(Value.FineWidth * Value.FineDepth)];
        Array.Fill(_activeSampleLookup, -1);
        for (var index = 0; index < _activeSamples.Length; index++)
        {
            var sample = _activeSamples[index];
            _activeSampleLookup[sample.FineZ * Value.FineWidth + sample.FineX] = index;
        }
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
    /// <param name="height">Initial height sample.</param>
    public static void SaveFlat(string path, int width = 65, int depth = 65, float height = 0.2f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!float.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(height));
        var heights = new float[checked(width * depth)];
        Array.Fill(heights, height);
        AssetDocumentStorage.WriteAtomic(path,
            stream => new TerrainResource(width, depth, heights).Save(stream));
    }
}
