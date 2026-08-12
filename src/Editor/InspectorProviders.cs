using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Identifies the editor used for one provider-created Inspector property.</summary>
public enum InspectorPropertyKind
{
    /// <summary>Single-line text or numeric value.</summary>
    Text,

    /// <summary>Boolean toggle value.</summary>
    Boolean
}

/// <summary>Describes one property displayed by the Inspector.</summary>
/// <param name="Name">Human-readable property name.</param>
/// <param name="Value">Formatted property value.</param>
/// <param name="SetValue">Optional persistent value writer; null makes the property read-only.</param>
/// <param name="Kind">Editor control kind.</param>
public sealed record InspectorProperty(
    string Name,
    string Value,
    Func<string, bool>? SetValue = null,
    InspectorPropertyKind Kind = InspectorPropertyKind.Text);

/// <summary>Describes provider-independent Inspector content.</summary>
/// <param name="Title">Type or category heading.</param>
/// <param name="DisplayName">Selected item's display name.</param>
/// <param name="Content">The single composable Inspector content tree.</param>
public sealed record InspectorDocument(
    string Title,
    string DisplayName,
    UIElement Content);

/// <summary>Renders common provider properties as one reusable Inspector content tree.</summary>
public sealed class PropertyInspectorContent : Panel
{
    private readonly IReadOnlyList<InspectorProperty> _properties;
    private readonly UITheme _theme;

    /// <summary>Creates common property content.</summary>
    /// <param name="properties">Ordered property bindings.</param>
    /// <param name="theme">Theme supplying visuals.</param>
    public PropertyInspectorContent(
        IReadOnlyList<InspectorProperty> properties,
        UITheme? theme = null)
        : base(new Color(0f, 0f, 0f), 0f, properties.Count * 38f)
    {
        _properties = properties ?? throw new ArgumentNullException(nameof(properties));
        _theme = theme ?? UITheme.Dark;
        PaintBackground = false;
        Build();
    }

    /// <summary>Builds typed property controls once from their common descriptors.</summary>
    private void Build()
    {
        for (var index = 0; index < _properties.Count; index++)
        {
            var property = _properties[index];
            var y = index * 38f;
            AddChild(new Label(property.Name, 82f, 30f)
            {
                ForegroundColor = _theme.TextSecondary,
                Margin = new Thickness(0f, y, 0f, 0f)
            });
            UIElement editor;
            if (property.Kind == InspectorPropertyKind.Boolean)
            {
                var toggle = new ToggleButton(0f, 30f, property.Name, _theme)
                {
                    Name = $"InspectorProperty{index}",
                    IsChecked = bool.TryParse(property.Value, out var value) && value,
                    IsEnabled = property.SetValue is not null,
                    Margin = new Thickness(82f, y, 0f, 0f)
                };
                if (property.SetValue is not null)
                    toggle.CheckedChanged += value => property.SetValue(value.ToString());
                editor = toggle;
            }
            else
            {
                var field = new TextField(0f, 30f, _theme)
                {
                    Name = $"InspectorProperty{index}",
                    Text = property.Value,
                    IsReadOnly = property.SetValue is null,
                    Margin = new Thickness(82f, y, 0f, 0f)
                };
                if (property.SetValue is not null)
                    field.ValueUpdateRequested += text => property.SetValue(text);
                editor = field;
            }
            editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            AddChild(editor);
        }
    }
}

/// <summary>Creates Inspector content for one family of selectable editor objects.</summary>
public interface IInspectorProvider
{
    /// <summary>Attempts to describe an editor selection.</summary>
    /// <param name="target">Selected editor object.</param>
    /// <param name="document">Created Inspector document when supported.</param>
    /// <returns>True when this provider supports the selection.</returns>
    bool TryCreate(object target, out InspectorDocument? document);
}

/// <summary>Resolves the first registered provider that supports an editor selection.</summary>
public sealed class InspectorProviderRegistry
{
    private readonly List<IInspectorProvider> _providers = [];

    /// <summary>Registers a provider after existing, more-specific providers.</summary>
    /// <param name="provider">Provider to register.</param>
    public void Register(IInspectorProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers.Add(provider);
    }

    /// <summary>Creates Inspector content for a selection.</summary>
    /// <param name="target">Selected editor object.</param>
    /// <param name="document">Resolved document when supported.</param>
    /// <returns>True when a provider accepted the selection.</returns>
    public bool TryCreate(object target, out InspectorDocument? document)
    {
        ArgumentNullException.ThrowIfNull(target);
        for (var index = 0; index < _providers.Count; index++)
        {
            if (_providers[index].TryCreate(target, out document))
                return true;
        }
        document = null;
        return false;
    }
}

/// <summary>Describes physical project files and directories.</summary>
public sealed class FileSystemInspectorProvider : IInspectorProvider
{
    private readonly AssetDatabase _database;

    /// <summary>Creates a project filesystem Inspector provider.</summary>
    /// <param name="database">Asset database used to expose import identity.</param>
    public FileSystemInspectorProvider(AssetDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <inheritdoc/>
    public bool TryCreate(object target, out InspectorDocument? document)
    {
        if (target is not FileSystemNode node)
        {
            document = null;
            return false;
        }
        var properties = new List<InspectorProperty>
        {
            new("Kind", node.IsDirectory ? "Folder" : "File"),
            new("Path", Path.GetRelativePath(_database.ProjectRoot, node.FullPath))
        };
        if (!node.IsDirectory)
        {
            var info = new FileInfo(node.FullPath);
            properties.Add(new InspectorProperty("Extension",
                string.IsNullOrEmpty(info.Extension) ? "None" : info.Extension));
            properties.Add(new InspectorProperty("Size", FormatSize(info.Length)));
            var record = _database.FindByPath(node.FullPath);
            properties.Add(new InspectorProperty("Importer", record?.Importer ?? "Unsupported"));
            if (record is not null)
                properties.Add(new InspectorProperty("Asset ID", record.Id.ToString()));
        }
        document = new InspectorDocument(node.IsDirectory ? "Folder" : "Asset", node.Name,
            new PropertyInspectorContent(properties));
        return true;
    }

    /// <summary>Formats a byte count for compact Inspector display.</summary>
    /// <param name="bytes">File size in bytes.</param>
    /// <returns>Human-readable binary size.</returns>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.##} KB";
        return $"{bytes / (1024d * 1024d):0.##} MB";
    }
}

/// <summary>Describes one imported resource beneath its physical source asset.</summary>
public sealed class ImportedSubAssetInspectorProvider : IInspectorProvider
{
    /// <inheritdoc/>
    public bool TryCreate(object target, out InspectorDocument? document)
    {
        if (target is not ImportedSubAssetNode node)
        {
            document = null;
            return false;
        }
        document = new InspectorDocument("Imported Resource", node.Name,
            new PropertyInspectorContent([
            new InspectorProperty("Content Type", node.ContentType),
            new InspectorProperty("Source", Path.GetFileName(node.SourcePath)),
            new InspectorProperty("Asset ID", node.Reference.Asset.ToString()),
            new InspectorProperty("Sub-asset", node.Reference.SubAsset ?? "main")
        ]));
        return true;
    }
}

/// <summary>Routes physical and imported assets through the content-type editor registry.</summary>
public sealed class AssetContentInspectorProvider : IInspectorProvider
{
    private readonly AssetDatabase _database;
    private readonly AssetEditorRegistry _editors;
    private readonly Func<float> _resolveWidth;

    /// <summary>Creates a generic asset-content provider.</summary>
    /// <param name="database">Project asset database.</param>
    /// <param name="editors">Content-type editor registry.</param>
    /// <param name="resolveWidth">Current Inspector content-width resolver.</param>
    public AssetContentInspectorProvider(
        AssetDatabase database,
        AssetEditorRegistry editors,
        Func<float> resolveWidth)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _editors = editors ?? throw new ArgumentNullException(nameof(editors));
        _resolveWidth = resolveWidth ?? throw new ArgumentNullException(nameof(resolveWidth));
    }

    /// <inheritdoc/>
    public bool TryCreate(object target, out InspectorDocument? document)
    {
        AssetReference reference;
        string name;
        if (target is ImportedSubAssetNode imported)
        {
            reference = imported.Reference;
            name = imported.Name;
        }
        else if (target is FileSystemNode { IsDirectory: false } file &&
                 _database.FindByPath(file.FullPath) is { } record)
        {
            reference = new AssetReference(record.Id, "main");
            name = file.Name;
        }
        else
        {
            document = null;
            return false;
        }
        try
        {
            var content = _editors.Create(reference, MathF.Max(0f, _resolveWidth()));
            if (content is null)
            {
                document = null;
                return false;
            }
            document = new InspectorDocument("Asset", name, content);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            document = new InspectorDocument("Asset", name,
                new PropertyInspectorContent([
                    new InspectorProperty("Error", exception.Message)]));
            return true;
        }
    }
}
