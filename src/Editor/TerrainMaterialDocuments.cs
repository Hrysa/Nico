using Engine.Core;

namespace Editor;

/// <summary>Loads editable terrain-layer documents.</summary>
public sealed class TerrainLayerDocumentFactory : IAssetDocumentFactory
{
    /// <inheritdoc/>
    public string ContentType => "nico/terrain-layer";

    /// <inheritdoc/>
    public IAssetDocument Load(AssetDocumentLocation location, Action<AssetReference> saved)
    {
        using var stream = location.OpenRead();
        return new TerrainLayerDocument(location,
            TerrainMaterialAssetCodec.LoadLayer(stream), saved);
    }
}

/// <summary>Loads editable painted terrain-material documents.</summary>
public sealed class TerrainMaterialDocumentFactory : IAssetDocumentFactory
{
    /// <inheritdoc/>
    public string ContentType => "nico/terrain-material";

    /// <inheritdoc/>
    public IAssetDocument Load(AssetDocumentLocation location, Action<AssetReference> saved)
    {
        using var stream = location.OpenRead();
        return new TerrainMaterialDocument(location,
            TerrainMaterialAssetCodec.LoadMaterial(stream), saved);
    }
}

/// <summary>Shared editable terrain-layer document.</summary>
public sealed class TerrainLayerDocument : IAssetDocument<TerrainLayerAsset>
{
    private readonly AssetDocumentLocation _location;
    private readonly Action<AssetReference> _saved;
    private bool _disposed;

    /// <inheritdoc/>
    public AssetReference Reference => _location.Reference;
    /// <inheritdoc/>
    public string DisplayName => _location.ResolveDisplayName();
    /// <inheritdoc/>
    public TerrainLayerAsset Value { get; private set; }
    object IAssetDocument.Value => Value;
    /// <inheritdoc/>
    public bool IsEditable => _location.IsEditable;
    /// <inheritdoc/>
    public bool IsDirty { get; private set; }
    /// <inheritdoc/>
    public Exception? LastError { get; private set; }
    /// <inheritdoc/>
    public event Action? Changed;

    /// <summary>Creates one loaded terrain-layer document.</summary>
    /// <param name="location">Current source location.</param>
    /// <param name="value">Decoded terrain layer.</param>
    /// <param name="saved">Callback invoked after persistence.</param>
    public TerrainLayerDocument(
        AssetDocumentLocation location,
        TerrainLayerAsset value,
        Action<AssetReference> saved)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
    }

    /// <inheritdoc/>
    public void MarkDirty()
    {
        EnsureEditable();
        IsDirty = true;
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Save()
    {
        EnsureEditable();
        try
        {
            _location.Write(stream => TerrainMaterialAssetCodec.SaveLayer(stream, Value));
            IsDirty = false;
            LastError = null;
            Changed?.Invoke();
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
        try
        {
            using var stream = _location.OpenRead();
            Value = TerrainMaterialAssetCodec.LoadLayer(stream);
            IsDirty = false;
            LastError = null;
        }
        catch (Exception exception)
        {
            LastError = exception;
        }
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Changed = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Requires a live editable document.</summary>
    private void EnsureEditable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEditable)
            throw new InvalidOperationException("This imported terrain layer is read-only.");
    }
}

/// <summary>Shared painted terrain material with undoable weight strokes.</summary>
public sealed class TerrainMaterialDocument : IAssetDocument<TerrainMaterialAsset>
{
    private readonly AssetDocumentLocation _location;
    private readonly Action<AssetReference> _saved;
    private readonly Stack<byte[]> _undo = [];
    private readonly Stack<byte[]> _redo = [];
    private byte[] _weights;
    private byte[]? _strokeBefore;
    private bool _strokeChanged;
    private bool _strokeWasDirty;
    private bool _disposed;

    /// <inheritdoc/>
    public AssetReference Reference => _location.Reference;
    /// <inheritdoc/>
    public string DisplayName => _location.ResolveDisplayName();
    /// <inheritdoc/>
    public TerrainMaterialAsset Value { get; private set; }
    object IAssetDocument.Value => Value;
    /// <inheritdoc/>
    public bool IsEditable => _location.IsEditable;
    /// <inheritdoc/>
    public bool IsDirty { get; private set; }
    /// <inheritdoc/>
    public Exception? LastError { get; private set; }
    /// <summary>Gets whether one paint stroke is active.</summary>
    public bool IsStrokeActive => _strokeBefore is not null;
    /// <summary>Gets whether a completed paint stroke can be restored.</summary>
    public bool CanUndo => _undo.Count > 0;
    /// <summary>Gets whether an undone paint stroke can be reapplied.</summary>
    public bool CanRedo => _redo.Count > 0;
    /// <inheritdoc/>
    public event Action? Changed;

    /// <summary>Creates one loaded terrain-material document.</summary>
    /// <param name="location">Current source location.</param>
    /// <param name="value">Decoded painted terrain material.</param>
    /// <param name="saved">Callback invoked after persistence.</param>
    public TerrainMaterialDocument(
        AssetDocumentLocation location,
        TerrainMaterialAsset value,
        Action<AssetReference> saved)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        _weights = value.CopyWeights();
    }

    /// <summary>Resizes paint storage to match its terrain sample grid.</summary>
    /// <param name="width">Terrain sample columns.</param>
    /// <param name="depth">Terrain sample rows.</param>
    public void EnsureDimensions(int width, int depth)
    {
        EnsureEditable();
        if (Value.Width == width && Value.Depth == depth)
            return;
        Value.Resize(width, depth);
        _weights = Value.CopyWeights();
        _undo.Clear();
        _redo.Clear();
        MarkDirty();
        Save();
    }

    /// <summary>Begins one undoable layer-paint stroke.</summary>
    public void BeginStroke()
    {
        EnsureEditable();
        if (_strokeBefore is not null)
            throw new InvalidOperationException("A terrain paint stroke is already active.");
        _strokeBefore = (byte[])_weights.Clone();
        _strokeChanged = false;
        _strokeWasDirty = IsDirty;
    }

    /// <summary>Paints one selected layer with a smooth radial brush.</summary>
    /// <param name="u">Normalized brush center along X.</param>
    /// <param name="v">Normalized brush center along Z.</param>
    /// <param name="radiusU">Normalized X radius.</param>
    /// <param name="radiusV">Normalized Z radius.</param>
    /// <param name="amount">Normalized paint influence.</param>
    /// <param name="layer">Selected layer channel.</param>
    /// <returns>Changed sample rectangle, or null when no weights changed.</returns>
    public TerrainEditRegion? ApplyPaint(
        float u,
        float v,
        float radiusU,
        float radiusV,
        float amount,
        int layer)
    {
        EnsureEditable();
        if (_strokeBefore is null)
            throw new InvalidOperationException("BeginStroke must be called before painting.");
        if (!float.IsFinite(u) || !float.IsFinite(v) || !float.IsFinite(radiusU) ||
            !float.IsFinite(radiusV) || radiusU <= 0f || radiusV <= 0f ||
            !float.IsFinite(amount) || amount <= 0f ||
            (uint)layer >= TerrainMaterialAsset.MaximumLayers || layer >= Value.Layers.Count)
            throw new ArgumentOutOfRangeException(nameof(layer));
        var minimumX = Math.Max(0, (int)MathF.Floor((u - radiusU) * (Value.Width - 1)));
        var maximumX = Math.Min(Value.Width - 1,
            (int)MathF.Ceiling((u + radiusU) * (Value.Width - 1)));
        var minimumZ = Math.Max(0, (int)MathF.Floor((v - radiusV) * (Value.Depth - 1)));
        var maximumZ = Math.Min(Value.Depth - 1,
            (int)MathF.Ceiling((v + radiusV) * (Value.Depth - 1)));
        var changedMinimumX = Value.Width;
        var changedMinimumZ = Value.Depth;
        var changedMaximumX = -1;
        var changedMaximumZ = -1;
        for (var z = minimumZ; z <= maximumZ; z++)
        {
            var distanceV = (z / (float)(Value.Depth - 1) - v) / radiusV;
            for (var x = minimumX; x <= maximumX; x++)
            {
                var distanceU = (x / (float)(Value.Width - 1) - u) / radiusU;
                var distanceSquared = distanceU * distanceU + distanceV * distanceV;
                if (distanceSquared > 1f)
                    continue;
                var falloff = 1f - MathF.Sqrt(distanceSquared);
                falloff = falloff * falloff * (3f - 2f * falloff);
                var influence = Math.Clamp(amount * falloff, 0f, 1f);
                var offset = (z * Value.Width + x) * TerrainMaterialAsset.MaximumLayers;
                var previous = _weights[offset + layer] / 255f;
                var selected = previous + (1f - previous) * influence;
                if (selected - previous <= 0.0001f)
                    continue;
                var otherTotal = 1f - previous;
                var remaining = 1f - selected;
                var assigned = 0;
                for (var channel = 0; channel < TerrainMaterialAsset.MaximumLayers; channel++)
                {
                    if (channel == layer)
                        continue;
                    var normalized = otherTotal <= 0.0001f ? 0f :
                        _weights[offset + channel] / 255f / otherTotal * remaining;
                    _weights[offset + channel] = (byte)Math.Clamp(
                        (int)MathF.Round(normalized * 255f), 0, 255 - assigned);
                    assigned += _weights[offset + channel];
                }
                _weights[offset + layer] = (byte)(255 - assigned);
                changedMinimumX = Math.Min(changedMinimumX, x);
                changedMinimumZ = Math.Min(changedMinimumZ, z);
                changedMaximumX = Math.Max(changedMaximumX, x);
                changedMaximumZ = Math.Max(changedMaximumZ, z);
            }
        }
        if (changedMaximumX < 0)
            return null;
        Value.UpdateWeights(_weights);
        _weights = Value.CopyWeights();
        _strokeChanged = true;
        IsDirty = true;
        LastError = null;
        return new TerrainEditRegion(changedMinimumX, changedMinimumZ,
            changedMaximumX, changedMaximumZ);
    }

    /// <summary>Completes an active paint stroke and optionally persists it.</summary>
    /// <param name="save">Whether changed weights should be saved.</param>
    public void EndStroke(bool save)
    {
        EnsureEditable();
        if (_strokeBefore is null)
            throw new InvalidOperationException("No terrain paint stroke is active.");
        if (_strokeChanged)
        {
            _undo.Push(_strokeBefore);
            _redo.Clear();
        }
        _strokeBefore = null;
        _strokeChanged = false;
        if (save && IsDirty)
            Save();
        else
            Changed?.Invoke();
    }

    /// <summary>Cancels one active paint stroke and restores its prior weights.</summary>
    /// <returns>True when changed weights were restored.</returns>
    public bool CancelStroke()
    {
        EnsureEditable();
        if (_strokeBefore is null)
            return false;
        var changed = _strokeChanged;
        if (changed)
        {
            _weights = _strokeBefore;
            Value.UpdateWeights(_weights);
        }
        _strokeBefore = null;
        _strokeChanged = false;
        IsDirty = _strokeWasDirty;
        Changed?.Invoke();
        return changed;
    }

    /// <inheritdoc/>
    public void MarkDirty()
    {
        EnsureEditable();
        IsDirty = true;
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Save()
    {
        EnsureEditable();
        try
        {
            Value.UpdateWeights(_weights);
            _location.Write(stream => TerrainMaterialAssetCodec.SaveMaterial(stream, Value));
            IsDirty = false;
            LastError = null;
            Changed?.Invoke();
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
        try
        {
            using var stream = _location.OpenRead();
            Value = TerrainMaterialAssetCodec.LoadMaterial(stream);
            _weights = Value.CopyWeights();
            _undo.Clear();
            _redo.Clear();
            IsDirty = false;
            LastError = null;
        }
        catch (Exception exception)
        {
            LastError = exception;
        }
        Changed?.Invoke();
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
        GC.SuppressFinalize(this);
    }

    /// <summary>Requires a live editable document.</summary>
    private void EnsureEditable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEditable)
            throw new InvalidOperationException("This imported terrain material is read-only.");
    }
}
