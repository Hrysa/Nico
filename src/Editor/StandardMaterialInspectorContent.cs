using System.Globalization;
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Shared standard-material editor embedded by asset and object Inspectors.</summary>
public sealed class StandardMaterialInspectorContent : Panel, IInspectorContentLifecycle
{
    private readonly StandardMaterialDocument _document;
    private readonly Func<AssetReference, string> _displayName;
    private readonly UITheme _theme;
    private readonly List<Action> _refresh = [];
    private readonly List<UIElement> _wideEditors = [];
    private readonly List<TextureEditorRow> _textureEditors = [];
    private Label? _status;
    private bool _active;

    /// <summary>Creates shared editable material content.</summary>
    /// <param name="width">Available content width.</param>
    /// <param name="document">Shared material document.</param>
    /// <param name="displayName">Asset-reference display-name resolver.</param>
    /// <param name="theme">Theme supplying visuals.</param>
    public StandardMaterialInspectorContent(
        float width,
        StandardMaterialDocument document,
        Func<AssetReference, string> displayName,
        UITheme? theme = null)
        : base(new Color(0f, 0f, 0f), width, 326f)
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
        _document.Changed += RefreshValues;
        RefreshValues();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _document.Changed -= RefreshValues;
    }

    /// <summary>Builds the common material controls.</summary>
    private void Build()
    {
        AddChild(Label("Base Color", 0f));
        const float left = 82f;
        var baseColor = new ColorPicker(Width - left, 30f, showAlpha: true, _theme)
        {
            Name = "MaterialBaseColor",
            Value = _document.Value.BaseColor,
            IsEnabled = _document.IsEditable,
            Margin = new Thickness(left, 0f, 0f, 0f)
        };
        if (_document.IsEditable)
            baseColor.ValueChanged += value =>
            {
                _document.Value.BaseColor = value;
                _document.MarkDirty();
                _document.Save();
            };
        _refresh.Add(() => baseColor.SetValueWithoutNotification(_document.Value.BaseColor));
        _wideEditors.Add(baseColor);
        AddChild(baseColor);
        AddFloat("Metallic", "MaterialMetallic", 38f, () => _document.Value.Metallic,
            value => _document.Value.Metallic = Math.Clamp(value, 0f, 1f));
        AddFloat("Roughness", "MaterialRoughness", 76f, () => _document.Value.Roughness,
            value => _document.Value.Roughness = Math.Clamp(value, 0f, 1f));
        AddTexture("Base Map", "MaterialBaseColorTexture", "No base-color texture", 114f,
            () => _document.Value.BaseColorTexture,
            value => _document.Value.BaseColorTexture = value);
        AddTexture("Normal", "MaterialNormalTexture", "No normal map", 152f,
            () => _document.Value.NormalTexture,
            value => _document.Value.NormalTexture = value);
        AddTexture("Metal/Rgh", "MaterialMetallicRoughnessTexture",
            "No metallic-roughness map", 190f,
            () => _document.Value.MetallicRoughnessTexture,
            value => _document.Value.MetallicRoughnessTexture = value);
        var doubleSided = new ToggleButton(Width - left, 30f, "Double Sided", _theme)
        {
            Name = "MaterialDoubleSided",
            IsChecked = _document.Value.DoubleSided,
            IsEnabled = _document.IsEditable,
            Margin = new Thickness(left, 228f, 0f, 0f)
        };
        _wideEditors.Add(doubleSided);
        if (_document.IsEditable)
            doubleSided.CheckedChanged += value =>
        {
            _document.Value.DoubleSided = value;
            _document.MarkDirty();
            _document.Save();
        };
        AddChild(Label("Sides", 228f));
        AddChild(doubleSided);
        _refresh.Add(() => doubleSided.IsChecked = _document.Value.DoubleSided);
        _status = new Label(string.Empty, Width, 30f)
        {
            Name = "MaterialDocumentStatus",
            ForegroundColor = _theme.TextMuted,
            Margin = new Thickness(0f, 274f, 0f, 0f)
        };
        AddChild(_status);
        _refresh.Add(() =>
        {
            _status.Text = _document.LastError?.Message ??
                (!_document.IsEditable ? "Read-only imported material" :
                    _document.IsDirty ? "Unsaved changes" : string.Empty);
            _status.ForegroundColor = _document.LastError is null
                ? _theme.TextMuted : _theme.Error;
        });
    }

    /// <summary>Adds one generic texture-reference material property.</summary>
    /// <param name="label">Property label.</param>
    /// <param name="name">Element name.</param>
    /// <param name="placeholder">Empty-value hint.</param>
    /// <param name="y">Row top.</param>
    /// <param name="read">Current reference reader.</param>
    /// <param name="write">Reference writer.</param>
    private void AddTexture(
        string label,
        string name,
        string placeholder,
        float y,
        Func<AssetReference?> read,
        Action<AssetReference?> write)
    {
        const float left = 82f;
        AddChild(Label(label, y));
        var textureWidth = _document.IsEditable ? Width - left - 58f : Width - left;
        var texture = new AssetReferenceField(textureWidth, 30f, "nico/texture2d", reference =>
        {
            if (!_document.IsEditable)
                return false;
            write(reference);
            _document.MarkDirty();
            _document.Save();
            return true;
        }, _theme)
        {
            Name = name,
            Text = read() is { } reference ? _displayName(reference) : string.Empty,
            Placeholder = placeholder,
            IsEnabled = _document.IsEditable,
            Margin = new Thickness(left, y, 0f, 0f)
        };
        AddChild(texture);
        Button? clear = null;
        if (_document.IsEditable)
        {
            clear = new Button(54f, 30f, "Clear", _theme)
            {
                Name = $"{name}Clear",
                Margin = new Thickness(Width - 54f, y, 0f, 0f)
            };
            clear.Click += () =>
            {
                write(null);
                _document.MarkDirty();
                _document.Save();
            };
            AddChild(clear);
        }
        _textureEditors.Add(new TextureEditorRow(texture, clear, y));
        _refresh.Add(() => texture.Text = read() is { } reference
            ? _displayName(reference) : string.Empty);
    }

    /// <summary>Adds one bounded floating-point material property.</summary>
    /// <param name="label">Property label.</param>
    /// <param name="name">Element name.</param>
    /// <param name="y">Row top.</param>
    /// <param name="read">Current value reader.</param>
    /// <param name="write">Validated value writer.</param>
    private void AddFloat(
        string label,
        string name,
        float y,
        Func<float> read,
        Action<float> write)
    {
        AddChild(Label(label, y));
        var field = new TextField(Width - 82f, 30f, _theme)
        {
            Name = name,
            Text = Format(read()),
            UpdateTrigger = TextUpdateTrigger.Commit,
            IsReadOnly = !_document.IsEditable,
            Margin = new Thickness(82f, y, 0f, 0f)
        };
        if (_document.IsEditable)
            field.ValueUpdateRequested += text => SetFloat(text, write);
        _refresh.Add(() => field.Text = Format(read()));
        _wideEditors.Add(field);
        AddChild(field);
    }

    /// <summary>Reflows material controls to the current Inspector width.</summary>
    /// <param name="contentSize">Current material content size.</param>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        const float left = 82f;
        var wideWidth = MathF.Max(0f, contentSize.X - left);
        for (var index = 0; index < _wideEditors.Count; index++)
            SetWidth(_wideEditors[index], wideWidth);
        for (var index = 0; index < _textureEditors.Count; index++)
        {
            var row = _textureEditors[index];
            SetWidth(row.Field, MathF.Max(0f,
                wideWidth - (row.ClearButton is null ? 0f : 58f)));
            if (row.ClearButton is not null)
            {
                var margin = new Thickness(
                    MathF.Max(0f, contentSize.X - 54f), row.Y, 0f, 0f);
                if (row.ClearButton.Margin != margin)
                    row.ClearButton.Margin = margin;
            }
        }
        if (_status is not null)
            SetWidth(_status, contentSize.X);
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.Measure(contentSize);
        }
        base.ArrangeOverride(contentSize);
    }

    /// <summary>Updates an arranged width only when it actually changed.</summary>
    /// <param name="element">Element to resize.</param>
    /// <param name="width">New nonnegative width.</param>
    private static void SetWidth(UIElement element, float width)
    {
        if (element.Width != width)
            element.Width = width;
    }

    /// <summary>Synchronizes all controls from the shared document.</summary>
    private void RefreshValues()
    {
        for (var index = 0; index < _refresh.Count; index++)
            _refresh[index]();
    }

    /// <summary>Validates, applies, and saves one floating-point edit.</summary>
    /// <param name="text">Candidate invariant numeric text.</param>
    /// <param name="write">Value writer.</param>
    private void SetFloat(string text, Action<float> write)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value) || !float.IsFinite(value))
        {
            RefreshValues();
            return;
        }
        write(value);
        _document.MarkDirty();
        _document.Save();
    }

    /// <summary>Creates one material-property label.</summary>
    /// <param name="text">Label text.</param>
    /// <param name="y">Row top.</param>
    /// <returns>The positioned label.</returns>
    private Label Label(string text, float y)
    {
        return new Label(text, 76f, 30f)
        {
            ForegroundColor = _theme.TextSecondary,
            Margin = new Thickness(0f, y, 0f, 0f)
        };
    }

    /// <summary>Formats a material scalar.</summary>
    /// <param name="value">Value to format.</param>
    /// <returns>Invariant compact text.</returns>
    private static string Format(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Tracks one responsive texture-reference row.</summary>
    /// <param name="Field">Asset-reference field.</param>
    /// <param name="ClearButton">Optional clear action.</param>
    /// <param name="Y">Row top.</param>
    private readonly record struct TextureEditorRow(
        AssetReferenceField Field,
        Button? ClearButton,
        float Y);
}
