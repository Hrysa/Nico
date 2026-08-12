using Engine.Core;
using Engine.Assets;
using Engine.UI;

namespace Editor;

/// <summary>Receives deterministic activation while Inspector content is actually hosted.</summary>
public interface IInspectorContentLifecycle
{
    /// <summary>Activates model subscriptions.</summary>
    void Activate();

    /// <summary>Deactivates model subscriptions.</summary>
    void Deactivate();
}

/// <summary>Pairs an asset output's content type with current document storage.</summary>
/// <param name="ContentType">Published runtime content type.</param>
/// <param name="Location">Current readable and optionally writable location.</param>
public sealed record ResolvedAssetDocument(
    string ContentType,
    AssetDocumentLocation Location);

/// <summary>Creates Inspector content for one typed asset document.</summary>
public interface IAssetInspectorFactory
{
    /// <summary>Gets the supported runtime content type.</summary>
    string ContentType { get; }

    /// <summary>Creates Inspector content for a shared document.</summary>
    /// <param name="document">Shared typed document.</param>
    /// <param name="context">Common Inspector services.</param>
    /// <returns>Reusable Inspector content.</returns>
    UIElement Create(IAssetDocument document, AssetInspectorContext context);
}

/// <summary>Provides common services to registered asset Inspector factories.</summary>
/// <param name="Width">Current available content width.</param>
/// <param name="ResolveDisplayName">Persistent reference display-name resolver.</param>
public sealed record AssetInspectorContext(
    float Width,
    Func<AssetReference, string> ResolveDisplayName);

/// <summary>Registers which source importers expose editable main artifacts by content type.</summary>
public sealed class EditableAssetSourceRegistry
{
    private readonly Dictionary<string, string> _importersByContentType =
        new(StringComparer.Ordinal);

    /// <summary>Registers one editable source importer.</summary>
    /// <param name="contentType">Published runtime content type.</param>
    /// <param name="importerId">Source importer identifier.</param>
    public void Register(string contentType, string importerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(importerId);
        _importersByContentType.Add(contentType, importerId);
    }

    /// <summary>Checks whether one output is the editable main artifact of its source.</summary>
    /// <param name="contentType">Published runtime content type.</param>
    /// <param name="importerId">Current source importer.</param>
    /// <param name="subAsset">Published sub-asset key.</param>
    /// <returns>True when the source itself stores this document content.</returns>
    public bool IsEditable(string contentType, string importerId, string? subAsset)
    {
        return (subAsset is null or "main") &&
            _importersByContentType.TryGetValue(contentType, out var expected) &&
            expected == importerId;
    }
}

/// <summary>Resolves documents and Inspector factories exclusively by runtime content type.</summary>
public sealed class AssetEditorRegistry
{
    private readonly AssetDocumentService _documents;
    private readonly Func<AssetReference, ResolvedAssetDocument?> _resolve;
    private readonly Func<AssetReference, string> _displayName;
    private readonly Dictionary<string, IAssetInspectorFactory> _inspectors =
        new(StringComparer.Ordinal);

    /// <summary>Creates an asset-editor registry.</summary>
    /// <param name="documents">Shared document service.</param>
    /// <param name="resolve">Asset output and storage resolver.</param>
    /// <param name="displayName">Persistent reference display-name resolver.</param>
    public AssetEditorRegistry(
        AssetDocumentService documents,
        Func<AssetReference, ResolvedAssetDocument?> resolve,
        Func<AssetReference, string> displayName)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
    }

    /// <summary>Registers one content-type Inspector factory.</summary>
    /// <param name="factory">Factory to register.</param>
    public void Register(IAssetInspectorFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _inspectors.Add(factory.ContentType, factory);
    }

    /// <summary>Creates content for one persistent asset output.</summary>
    /// <param name="reference">Persistent asset reference.</param>
    /// <param name="width">Current available width.</param>
    /// <returns>Inspector content, or null when no editor is registered.</returns>
    public UIElement? Create(AssetReference reference, float width)
    {
        var resolved = _resolve(reference);
        if (resolved is null || !_inspectors.TryGetValue(resolved.ContentType, out var factory))
            return null;
        var document = _documents.GetOrLoad(resolved.Location, resolved.ContentType);
        return document is null
            ? null
            : factory.Create(document, new AssetInspectorContext(width, _displayName));
    }
}

/// <summary>Creates shared standard-material Inspector content.</summary>
public sealed class StandardMaterialInspectorFactory : IAssetInspectorFactory
{
    /// <inheritdoc/>
    public string ContentType => "nico/standard-material";

    /// <inheritdoc/>
    public UIElement Create(IAssetDocument document, AssetInspectorContext context)
    {
        if (document is not StandardMaterialDocument material)
            throw new InvalidOperationException("Standard-material editor received an invalid document.");
        return new StandardMaterialInspectorContent(
            context.Width, material, context.ResolveDisplayName);
    }
}

/// <summary>Provides common atomic source-file replacement for asset documents.</summary>
public static class AssetDocumentStorage
{
    /// <summary>Writes one source file atomically.</summary>
    /// <param name="path">Destination source path.</param>
    /// <param name="write">Content writer.</param>
    public static void WriteAtomic(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Asset path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                write(stream);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

/// <summary>Resolves physical and imported Editor tree payloads to typed asset references.</summary>
public sealed class AssetDropResolver
{
    private readonly AssetDatabase _database;
    private readonly AssetImportPipeline _pipeline;

    /// <summary>Creates a common Editor asset-drop resolver.</summary>
    /// <param name="database">Project asset database.</param>
    /// <param name="pipeline">Asset import pipeline.</param>
    public AssetDropResolver(AssetDatabase database, AssetImportPipeline pipeline)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    /// <summary>Resolves one tree item to a compatible persistent asset output.</summary>
    /// <param name="item">Dragged tree item.</param>
    /// <param name="contentType">Required runtime content type.</param>
    /// <param name="reference">Resolved reference when accepted.</param>
    /// <returns>True when one unambiguous compatible output was resolved.</returns>
    public bool TryResolve(object item, string contentType, out AssetReference reference)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (item is ImportedSubAssetNode imported && imported.ContentType == contentType)
        {
            reference = imported.Reference;
            return true;
        }
        if (item is not FileSystemNode { IsDirectory: false } file ||
            _database.FindByPath(file.FullPath) is not { } record)
        {
            reference = default;
            return false;
        }
        var outcome = _pipeline.Import(record, "editor");
        AssetArtifact? main = null;
        AssetArtifact? single = null;
        var count = 0;
        for (var index = 0; index < outcome.Artifacts.Count; index++)
        {
            var candidate = outcome.Artifacts[index];
            if (candidate.ContentType != contentType)
                continue;
            count++;
            single = candidate;
            if (candidate.Key == "main")
                main = candidate;
        }
        var match = main ?? (count == 1 ? single : null);
        if (!outcome.Succeeded || match is null)
        {
            reference = default;
            return false;
        }
        reference = new AssetReference(record.Id, match.Key);
        return true;
    }
}
