using Engine.Core;

namespace Editor;

/// <summary>Provides current storage and editability for one persistent asset output.</summary>
public sealed class AssetDocumentLocation
{
    private readonly Func<Stream> _openRead;
    private readonly Action<Action<Stream>>? _write;

    /// <summary>Gets the persistent asset reference.</summary>
    public AssetReference Reference { get; }

    /// <summary>Gets the display name resolved from current project state.</summary>
    public Func<string> ResolveDisplayName { get; }

    /// <summary>Gets whether the location permits source edits.</summary>
    public bool IsEditable => _write is not null;

    /// <summary>Creates one current asset-document location.</summary>
    /// <param name="reference">Persistent asset reference.</param>
    /// <param name="resolveDisplayName">Current display-name resolver.</param>
    /// <param name="openRead">Opens current readable content.</param>
    /// <param name="write">Optionally writes content atomically.</param>
    public AssetDocumentLocation(
        AssetReference reference,
        Func<string> resolveDisplayName,
        Func<Stream> openRead,
        Action<Action<Stream>>? write = null)
    {
        Reference = reference;
        ResolveDisplayName = resolveDisplayName
            ?? throw new ArgumentNullException(nameof(resolveDisplayName));
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        _write = write;
    }

    /// <summary>Opens current readable content.</summary>
    /// <returns>Owned readable stream.</returns>
    public Stream OpenRead() => _openRead();

    /// <summary>Writes current content through the location's atomic writer.</summary>
    /// <param name="write">Content writer.</param>
    public void Write(Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (_write is null)
            throw new InvalidOperationException("This imported asset output is read-only.");
        _write(write);
    }
}

/// <summary>Represents one shared editable or read-only asset document.</summary>
public interface IAssetDocument : IDisposable
{
    /// <summary>Gets the persistent asset reference.</summary>
    AssetReference Reference { get; }

    /// <summary>Gets the current display name.</summary>
    string DisplayName { get; }

    /// <summary>Gets the current content value.</summary>
    object Value { get; }

    /// <summary>Gets whether the source permits edits.</summary>
    bool IsEditable { get; }

    /// <summary>Gets whether in-memory content differs from storage.</summary>
    bool IsDirty { get; }

    /// <summary>Gets the most recent save or reload error.</summary>
    Exception? LastError { get; }

    /// <summary>Occurs after content, dirty state, or error state changes.</summary>
    event Action? Changed;

    /// <summary>Marks current content dirty after a binding edit.</summary>
    void MarkDirty();

    /// <summary>Saves current content atomically.</summary>
    void Save();

    /// <summary>Reloads current content from its current asset location.</summary>
    void Reload();
}

/// <summary>Represents one shared asset with strongly typed content.</summary>
/// <typeparam name="TAsset">Asset content type.</typeparam>
public interface IAssetDocument<out TAsset> : IAssetDocument
{
    /// <summary>Gets typed current content.</summary>
    new TAsset Value { get; }
}

/// <summary>Loads an asset document for one registered content type.</summary>
public interface IAssetDocumentFactory
{
    /// <summary>Gets the supported runtime content type.</summary>
    string ContentType { get; }

    /// <summary>Loads one document.</summary>
    /// <param name="location">Current asset storage.</param>
    /// <param name="saved">Callback invoked after persistence.</param>
    /// <returns>Loaded document.</returns>
    IAssetDocument Load(AssetDocumentLocation location, Action<AssetReference> saved);
}

/// <summary>Caches documents and dispatches registered content-type factories.</summary>
public sealed class AssetDocumentService : IDisposable
{
    private readonly Dictionary<string, IAssetDocumentFactory> _factories =
        new(StringComparer.Ordinal);
    private readonly Dictionary<AssetReference, IAssetDocument> _documents = [];
    private readonly Action<AssetReference> _saved;
    private bool _disposed;

    /// <summary>Creates an asset-document service.</summary>
    /// <param name="saved">Callback invoked after any document is persisted.</param>
    public AssetDocumentService(Action<AssetReference> saved)
    {
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
    }

    /// <summary>Registers one content-type document factory.</summary>
    /// <param name="factory">Factory to register.</param>
    public void Register(IAssetDocumentFactory factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(factory);
        _factories.Add(factory.ContentType, factory);
    }

    /// <summary>Gets or loads one shared document.</summary>
    /// <param name="location">Current asset location.</param>
    /// <param name="contentType">Published runtime content type.</param>
    /// <returns>Shared document, or null when the type has no factory.</returns>
    public IAssetDocument? GetOrLoad(AssetDocumentLocation location, string contentType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(location);
        if (_documents.TryGetValue(location.Reference, out var existing))
            return existing;
        if (!_factories.TryGetValue(contentType, out var factory))
            return null;
        var document = factory.Load(location, _saved);
        _documents.Add(location.Reference, document);
        return document;
    }

    /// <summary>Disposes every cached document and factory-independent subscription.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var document in _documents.Values)
            document.Dispose();
        _documents.Clear();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Loads standard-material documents.</summary>
public sealed class StandardMaterialDocumentFactory : IAssetDocumentFactory
{
    /// <inheritdoc/>
    public string ContentType => "nico/standard-material";

    /// <inheritdoc/>
    public IAssetDocument Load(AssetDocumentLocation location, Action<AssetReference> saved)
    {
        using var stream = location.OpenRead();
        return new StandardMaterialDocument(
            location, StandardMaterialAssetCodec.Load(stream), saved);
    }
}

/// <summary>Shared standard-material document.</summary>
public sealed class StandardMaterialDocument : IAssetDocument<StandardMaterialAsset>
{
    private readonly AssetDocumentLocation _location;
    private readonly Action<AssetReference> _saved;
    private bool _disposed;

    /// <inheritdoc/>
    public AssetReference Reference => _location.Reference;

    /// <inheritdoc/>
    public string DisplayName => _location.ResolveDisplayName();

    /// <inheritdoc/>
    public StandardMaterialAsset Value { get; private set; }

    object IAssetDocument.Value => Value;

    /// <inheritdoc/>
    public bool IsEditable => _location.IsEditable;

    /// <inheritdoc/>
    public bool IsDirty { get; private set; }

    /// <inheritdoc/>
    public Exception? LastError { get; private set; }

    /// <inheritdoc/>
    public event Action? Changed;

    /// <summary>Creates one loaded standard-material document.</summary>
    /// <param name="location">Current storage location.</param>
    /// <param name="value">Loaded material data.</param>
    /// <param name="saved">Callback invoked after persistence.</param>
    public StandardMaterialDocument(
        AssetDocumentLocation location,
        StandardMaterialAsset value,
        Action<AssetReference> saved)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
    }

    /// <inheritdoc/>
    public void MarkDirty()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEditable)
            throw new InvalidOperationException("This imported material is read-only.");
        IsDirty = true;
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public void Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEditable)
            throw new InvalidOperationException("This imported material is read-only.");
        try
        {
            _location.Write(stream => StandardMaterialAssetCodec.Save(stream, Value));
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
        try
        {
            using var stream = _location.OpenRead();
            Value = StandardMaterialAssetCodec.Load(stream);
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
}
