using System.Globalization;
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Creates shared terrain-layer Inspector content.</summary>
public sealed class TerrainLayerInspectorFactory : IAssetInspectorFactory
{
    /// <inheritdoc/>
    public string ContentType => "nico/terrain-layer";

    /// <inheritdoc/>
    public UIElement Create(IAssetDocument document, AssetInspectorContext context)
    {
        if (document is not TerrainLayerDocument layer)
            throw new ArgumentException("Terrain layer content requires its typed document.",
                nameof(document));
        return new TerrainLayerInspectorContent(context.Width, layer,
            context.ResolveDisplayName);
    }
}

/// <summary>Creates shared painted terrain-material Inspector content.</summary>
public sealed class TerrainMaterialInspectorFactory : IAssetInspectorFactory
{
    private readonly TerrainBrushSettings _settings;

    /// <summary>Creates a factory sharing the Scene terrain tool settings.</summary>
    /// <param name="settings">Shared terrain brush settings.</param>
    public TerrainMaterialInspectorFactory(TerrainBrushSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc/>
    public string ContentType => "nico/terrain-material";

    /// <inheritdoc/>
    public UIElement Create(IAssetDocument document, AssetInspectorContext context)
    {
        if (document is not TerrainMaterialDocument material)
            throw new ArgumentException("Terrain material content requires its typed document.",
                nameof(document));
        return new TerrainMaterialInspectorContent(context.Width, material, _settings,
            context.ResolveDisplayName);
    }
}

/// <summary>Edits one tileable terrain PBR layer.</summary>
public sealed class TerrainLayerInspectorContent : Panel, IInspectorContentLifecycle
{
    private readonly TerrainLayerDocument _document;
    private readonly Func<AssetReference, string> _displayName;
    private readonly UITheme _theme;
    private readonly List<Action> _refresh = [];
    private bool _active;

    /// <summary>Creates terrain-layer controls.</summary>
    /// <param name="width">Available content width.</param>
    /// <param name="document">Shared terrain-layer document.</param>
    /// <param name="displayName">Reference display-name resolver.</param>
    /// <param name="theme">Optional UI theme.</param>
    public TerrainLayerInspectorContent(
        float width,
        TerrainLayerDocument document,
        Func<AssetReference, string> displayName,
        UITheme? theme = null)
        : base(new Color(0f, 0f, 0f), width, 342f)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        _theme = theme ?? UITheme.Dark;
        PaintBackground = false;
        Build();
    }

    /// <inheritdoc/>
    public void Activate()
    {
        if (_active)
            return;
        _active = true;
        _document.Changed += Refresh;
        Refresh();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _document.Changed -= Refresh;
    }

    /// <summary>Builds all layer controls.</summary>
    private void Build()
    {
        AddColor("Color", "TerrainLayerColor", 0f,
            () => _document.Value.BaseColor,
            value => _document.Value.BaseColor = value);
        AddFloat("Metallic", "TerrainLayerMetallic", 38f,
            () => _document.Value.Metallic,
            value => _document.Value.Metallic = Math.Clamp(value, 0f, 1f));
        AddFloat("Roughness", "TerrainLayerRoughness", 76f,
            () => _document.Value.Roughness,
            value => _document.Value.Roughness = Math.Clamp(value, 0f, 1f));
        AddVector2("Tiling", "TerrainLayerTiling", 114f,
            () => _document.Value.Tiling,
            value => _document.Value.Tiling = new Vector2(
                MathF.Max(0.001f, value.X), MathF.Max(0.001f, value.Y)));
        AddTexture("Base Map", "TerrainLayerBaseColorTexture", 152f,
            () => _document.Value.BaseColorTexture,
            value => _document.Value.BaseColorTexture = value);
        AddTexture("Normal", "TerrainLayerNormalTexture", 190f,
            () => _document.Value.NormalTexture,
            value => _document.Value.NormalTexture = value);
        AddTexture("Metal/Rgh", "TerrainLayerMetallicRoughnessTexture", 228f,
            () => _document.Value.MetallicRoughnessTexture,
            value => _document.Value.MetallicRoughnessTexture = value);
        var status = new Label(string.Empty, Width, 30f)
        {
            Name = "TerrainLayerDocumentStatus",
            ForegroundColor = _theme.TextMuted,
            Margin = new Thickness(0f, 282f, 0f, 0f)
        };
        AddChild(status);
        _refresh.Add(() => status.Text = _document.LastError?.Message ??
            (!_document.IsEditable ? "Read-only imported terrain layer" : string.Empty));
    }

    /// <summary>Adds a linear RGBA color editor.</summary>
    /// <param name="label">Displayed label.</param>
    /// <param name="name">Element-name prefix.</param>
    /// <param name="y">Row top.</param>
    /// <param name="read">Current value reader.</param>
    /// <param name="write">Value writer.</param>
    private void AddColor(string label, string name, float y,
        Func<Vector4> read, Action<Vector4> write)
    {
        AddChild(CreateLabel(label, y));
        var picker = new ColorPicker(Width - 82f, 30f, showAlpha: true, _theme)
        {
            Name = name,
            Value = read(),
            IsEnabled = _document.IsEditable,
            Margin = new Thickness(82f, y, 0f, 0f)
        };
        if (_document.IsEditable)
            picker.ValueChanged += value =>
            {
                write(value);
                Persist();
            };
        _refresh.Add(() => picker.SetValueWithoutNotification(read()));
        AddChild(picker);
    }

    /// <summary>Adds a two-component positive vector editor.</summary>
    /// <param name="label">Displayed label.</param>
    /// <param name="name">Element-name prefix.</param>
    /// <param name="y">Row top.</param>
    /// <param name="read">Current value reader.</param>
    /// <param name="write">Value writer.</param>
    private void AddVector2(string label, string name, float y,
        Func<Vector2> read, Action<Vector2> write)
    {
        AddChild(CreateLabel(label, y));
        var width = (Width - 86f) * 0.5f;
        var x = CreateField(name + "X", width, y, () => read().X,
            value => write(read() with { X = value }));
        x.Margin = new Thickness(82f, y, 0f, 0f);
        AddChild(x);
        var z = CreateField(name + "Y", width, y, () => read().Y,
            value => write(read() with { Y = value }));
        z.Margin = new Thickness(86f + width, y, 0f, 0f);
        AddChild(z);
    }

    /// <summary>Adds one scalar editor.</summary>
    /// <param name="label">Displayed label.</param>
    /// <param name="name">Element name.</param>
    /// <param name="y">Row top.</param>
    /// <param name="read">Current value reader.</param>
    /// <param name="write">Value writer.</param>
    private void AddFloat(string label, string name, float y,
        Func<float> read, Action<float> write)
    {
        AddChild(CreateLabel(label, y));
        var field = CreateField(name, Width - 82f, y, read, write);
        field.Margin = new Thickness(82f, y, 0f, 0f);
        AddChild(field);
    }

    /// <summary>Creates a persisted float field.</summary>
    /// <param name="name">Element name.</param>
    /// <param name="width">Field width.</param>
    /// <param name="y">Row top.</param>
    /// <param name="read">Current value reader.</param>
    /// <param name="write">Value writer.</param>
    /// <returns>Configured field.</returns>
    private TextField CreateField(string name, float width, float y,
        Func<float> read, Action<float> write)
    {
        var field = new TextField(width, 30f, _theme)
        {
            Name = name,
            Text = Format(read()),
            IsReadOnly = !_document.IsEditable,
            UpdateTrigger = TextUpdateTrigger.Commit
        };
        if (_document.IsEditable)
        {
            field.ValueUpdateRequested += text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var value) || !float.IsFinite(value))
                {
                    Refresh();
                    return;
                }
                write(value);
                Persist();
            };
        }
        _refresh.Add(() => field.Text = Format(read()));
        return field;
    }

    /// <summary>Adds one texture-reference row.</summary>
    /// <param name="label">Displayed label.</param>
    /// <param name="name">Element name.</param>
    /// <param name="y">Row top.</param>
    /// <param name="read">Reference reader.</param>
    /// <param name="write">Reference writer.</param>
    private void AddTexture(string label, string name, float y,
        Func<AssetReference?> read, Action<AssetReference?> write)
    {
        AddChild(CreateLabel(label, y));
        var field = new AssetReferenceField(Width - 140f, 30f, "nico/texture2d", reference =>
        {
            if (!_document.IsEditable)
                return false;
            write(reference);
            Persist();
            return true;
        }, _theme)
        {
            Name = name,
            Text = read() is { } reference ? _displayName(reference) : string.Empty,
            IsEnabled = _document.IsEditable,
            Margin = new Thickness(82f, y, 0f, 0f)
        };
        AddChild(field);
        _refresh.Add(() => field.Text = read() is { } reference
            ? _displayName(reference) : string.Empty);
        if (!_document.IsEditable)
            return;
        var clear = new Button(54f, 30f, "Clear", _theme)
        {
            Name = name + "Clear",
            Margin = new Thickness(Width - 54f, y, 0f, 0f)
        };
        clear.Click += () =>
        {
            write(null);
            Persist();
        };
        AddChild(clear);
    }

    /// <summary>Marks and saves the shared document.</summary>
    private void Persist()
    {
        _document.MarkDirty();
        _document.Save();
    }

    /// <summary>Refreshes controls from current document values.</summary>
    private void Refresh()
    {
        for (var index = 0; index < _refresh.Count; index++)
            _refresh[index]();
    }

    /// <summary>Creates one property label.</summary>
    /// <param name="text">Displayed text.</param>
    /// <param name="y">Row top.</param>
    /// <returns>Configured label.</returns>
    private Label CreateLabel(string text, float y) => new(text, 78f, 30f)
    {
        ForegroundColor = _theme.TextSecondary,
        Margin = new Thickness(0f, y, 0f, 0f)
    };

    /// <summary>Formats one scalar.</summary>
    /// <param name="value">Scalar value.</param>
    /// <returns>Invariant compact text.</returns>
    private static string Format(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

}

/// <summary>Edits ordered terrain layers and controls Scene layer painting.</summary>
public sealed class TerrainMaterialInspectorContent : Panel, IInspectorContentLifecycle
{
    private readonly TerrainMaterialDocument _document;
    private readonly TerrainBrushSettings _settings;
    private readonly Func<AssetReference, string> _displayName;
    private readonly List<Action> _refresh = [];
    private readonly ToggleButton[] _layerButtons = new ToggleButton[4];
    private bool _active;

    /// <summary>Creates painted terrain-material controls.</summary>
    /// <param name="width">Available width.</param>
    /// <param name="document">Shared painted material document.</param>
    /// <param name="settings">Shared Scene brush settings.</param>
    /// <param name="displayName">Reference display-name resolver.</param>
    /// <param name="theme">Optional UI theme.</param>
    public TerrainMaterialInspectorContent(float width, TerrainMaterialDocument document,
        TerrainBrushSettings settings, Func<AssetReference, string> displayName,
        UITheme? theme = null)
        : base(new Color(0f, 0f, 0f), width, 310f)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _displayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        var resolvedTheme = theme ?? UITheme.Dark;
        PaintBackground = false;
        for (var index = 0; index < TerrainMaterialAsset.MaximumLayers; index++)
            AddLayerRow(index, 38f * index, resolvedTheme);
        var paint = new ToggleButton(width, 30f, "Paint Layers in Scene", resolvedTheme)
        {
            Name = "TerrainPaintEnabled",
            IsChecked = _settings.IsEnabled && _settings.ToolMode == TerrainToolMode.Paint,
            Margin = new Thickness(0f, 164f, 0f, 0f)
        };
        paint.CheckedChanged += value =>
        {
            _settings.ToolMode = TerrainToolMode.Paint;
            _settings.IsEnabled = value;
        };
        AddChild(paint);
        var buttonWidth = (width - 12f) / 4f;
        for (var index = 0; index < _layerButtons.Length; index++)
        {
            var layer = index;
            var button = new ToggleButton(buttonWidth, 30f, $"Layer {index + 1}", resolvedTheme)
            {
                Name = $"TerrainPaintLayer{index}",
                IsChecked = _settings.PaintLayer == index,
                Margin = new Thickness(index * (buttonWidth + 4f), 202f, 0f, 0f)
            };
            button.Click += () => _settings.PaintLayer = layer;
            _layerButtons[index] = button;
            AddChild(button);
        }
        var status = new Label(string.Empty, width, 30f)
        {
            Name = "TerrainMaterialDocumentStatus",
            ForegroundColor = resolvedTheme.TextMuted,
            Margin = new Thickness(0f, 248f, 0f, 0f)
        };
        AddChild(status);
        _refresh.Add(() => status.Text = _document.LastError?.Message ??
            $"{_document.Value.Width} x {_document.Value.Depth} paint map");
    }

    /// <inheritdoc/>
    public void Activate()
    {
        if (_active)
            return;
        _active = true;
        _document.Changed += Refresh;
        _settings.Changed += Refresh;
        Refresh();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _document.Changed -= Refresh;
        _settings.Changed -= Refresh;
    }

    /// <summary>Adds one ordered terrain-layer reference row.</summary>
    /// <param name="index">Layer slot.</param>
    /// <param name="y">Row top.</param>
    /// <param name="theme">Resolved UI theme.</param>
    private void AddLayerRow(int index, float y, UITheme theme)
    {
        var field = new AssetReferenceField(Width - 62f, 30f, "nico/terrain-layer", reference =>
        {
            if (!_document.IsEditable || index > _document.Value.Layers.Count)
                return false;
            if (index == _document.Value.Layers.Count)
                _document.Value.Layers.Add(reference);
            else
                _document.Value.Layers[index] = reference;
            Persist();
            return true;
        }, theme)
        {
            Name = $"TerrainMaterialLayer{index}",
            Placeholder = index == 0 ? "Drop first terrain layer" : $"Drop layer {index + 1}",
            IsEnabled = _document.IsEditable,
            Margin = new Thickness(0f, y, 0f, 0f)
        };
        AddChild(field);
        _refresh.Add(() => field.Text = index < _document.Value.Layers.Count
            ? _displayName(_document.Value.Layers[index]) : string.Empty);
        if (!_document.IsEditable)
            return;
        var clear = new Button(58f, 30f, "Clear", theme)
        {
            Name = $"TerrainMaterialLayer{index}Clear",
            Margin = new Thickness(Width - 58f, y, 0f, 0f)
        };
        clear.Click += () =>
        {
            if (index >= _document.Value.Layers.Count)
                return;
            _document.Value.Layers.RemoveAt(index);
            if (_settings.PaintLayer >= _document.Value.Layers.Count)
                _settings.PaintLayer = Math.Max(0, _document.Value.Layers.Count - 1);
            Persist();
        };
        AddChild(clear);
    }

    /// <summary>Marks and saves layer-order edits.</summary>
    private void Persist()
    {
        _document.MarkDirty();
        _document.Save();
    }

    /// <summary>Synchronizes controls from the document and shared brush state.</summary>
    private void Refresh()
    {
        for (var index = 0; index < _refresh.Count; index++)
            _refresh[index]();
        for (var index = 0; index < _layerButtons.Length; index++)
        {
            _layerButtons[index].IsChecked = _settings.PaintLayer == index;
            _layerButtons[index].IsEnabled = index < _document.Value.Layers.Count;
        }
    }
}
