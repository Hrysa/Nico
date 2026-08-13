using System.Globalization;
using System.Numerics;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Creates the shared terrain asset editor used by file and scene Inspectors.</summary>
public sealed class TerrainInspectorFactory : IAssetInspectorFactory
{
    private readonly TerrainBrushSettings _settings;

    /// <summary>Creates a factory over the Scene viewport's shared brush settings.</summary>
    /// <param name="settings">Shared terrain-tool state.</param>
    public TerrainInspectorFactory(TerrainBrushSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc/>
    public string ContentType => "nico/terrain";

    /// <inheritdoc/>
    public UIElement Create(IAssetDocument document, AssetInspectorContext context)
    {
        if (document is not TerrainDocument terrain)
            throw new InvalidOperationException("Terrain editor received an invalid document.");
        return new TerrainInspectorContent(context.Width, terrain, _settings);
    }
}

/// <summary>Shared terrain sculpt controls embedded by asset and scene Inspectors.</summary>
public sealed class TerrainInspectorContent : Panel, IInspectorContentLifecycle
{
    private readonly TerrainDocument _document;
    private readonly TerrainBrushSettings _settings;
    private readonly UITheme _theme;
    private readonly ToggleButton[] _modeButtons = new ToggleButton[4];
    private readonly Button[] _actionButtons;
    private readonly ToggleButton _sculpt;
    private readonly TextField _radius;
    private readonly TextField _strength;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _save;
    private readonly Button _reload;
    private readonly Label _dimensions;
    private readonly Label _status;
    private bool _refreshing;
    private bool _active;

    /// <summary>Creates terrain document and Scene brush controls.</summary>
    /// <param name="width">Available Inspector width.</param>
    /// <param name="document">Shared terrain document.</param>
    /// <param name="settings">Shared Scene brush settings.</param>
    /// <param name="theme">Theme supplying visuals.</param>
    public TerrainInspectorContent(
        float width,
        TerrainDocument document,
        TerrainBrushSettings settings,
        UITheme? theme = null)
        : base(new Color(0f, 0f, 0f), width, 304f)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _theme = theme ?? UITheme.Dark;
        PaintBackground = false;

        _dimensions = new Label(string.Empty, width, 26f)
        {
            Name = "TerrainDimensions",
            ForegroundColor = _theme.TextSecondary,
            PaddingLeft = 0f
        };
        AddChild(_dimensions);

        _sculpt = new ToggleButton(width, 30f, "Sculpt in Scene", _theme,
            ButtonStyle.Primary)
        {
            Name = "TerrainSculptEnabled",
            IsEnabled = document.IsEditable,
            Margin = new Thickness(0f, 34f, 0f, 0f)
        };
        _sculpt.CheckedChanged += value =>
        {
            if (!_refreshing)
                _settings.IsEnabled = value;
        };
        AddChild(_sculpt);

        var modes = Enum.GetValues<TerrainBrushMode>();
        for (var index = 0; index < modes.Length; index++)
        {
            var mode = modes[index];
            var button = new ToggleButton(0f, 30f, mode.ToString(), _theme)
            {
                Name = $"TerrainBrush{mode}",
                IsEnabled = document.IsEditable,
                Margin = new Thickness(0f, 72f, 0f, 0f)
            };
            button.CheckedChanged += value =>
            {
                if (!_refreshing && value)
                    _settings.Mode = mode;
            };
            _modeButtons[index] = button;
            AddChild(button);
        }

        AddChild(CreateLabel("Radius", 110f));
        _radius = CreateFloatField("TerrainBrushRadius", 110f, _settings.Radius,
            value => _settings.Radius = value);
        AddChild(_radius);
        AddChild(CreateLabel("Strength", 148f));
        _strength = CreateFloatField("TerrainBrushStrength", 148f, _settings.Strength,
            value => _settings.Strength = value);
        AddChild(_strength);
        _undo = new Button(0f, 30f, "Undo", _theme) { Name = "TerrainUndo" };
        _redo = new Button(0f, 30f, "Redo", _theme) { Name = "TerrainRedo" };
        _save = new Button(0f, 30f, "Save", _theme, ButtonStyle.Primary)
            { Name = "TerrainSave" };
        _reload = new Button(0f, 30f, "Reload", _theme) { Name = "TerrainReload" };
        _actionButtons = [_undo, _redo, _save, _reload];
        for (var index = 0; index < _actionButtons.Length; index++)
        {
            _actionButtons[index].Margin = new Thickness(0f, 190f, 0f, 0f);
            AddChild(_actionButtons[index]);
        }
        _undo.Click += () =>
        {
            if (_document.Undo())
                _document.Save();
        };
        _redo.Click += () =>
        {
            if (_document.Redo())
                _document.Save();
        };
        _save.Click += _document.Save;
        _reload.Click += _document.Reload;

        _status = new Label(string.Empty, width, 54f)
        {
            Name = "TerrainDocumentStatus",
            ForegroundColor = _theme.TextMuted,
            PaddingLeft = 0f,
            Margin = new Thickness(0f, 232f, 0f, 0f)
        };
        AddChild(_status);
        Reflow(width);
        RefreshValues();
    }

    /// <inheritdoc/>
    public void Activate()
    {
        if (_active)
            return;
        _active = true;
        _document.Changed += RefreshValues;
        _settings.Changed += RefreshValues;
        RefreshValues();
    }

    /// <inheritdoc/>
    public void Deactivate()
    {
        if (!_active)
            return;
        _active = false;
        _document.Changed -= RefreshValues;
        _settings.Changed -= RefreshValues;
    }

    /// <summary>Reflows terrain controls to the current Inspector width.</summary>
    /// <param name="contentSize">Current terrain content size.</param>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        Reflow(contentSize.X);
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.Measure(contentSize);
        }
        base.ArrangeOverride(contentSize);
    }

    /// <summary>Positions equal-width tool groups and stretchable fields.</summary>
    /// <param name="width">Available content width.</param>
    private void Reflow(float width)
    {
        SetWidth(_dimensions, width);
        SetWidth(_status, width);
        SetWidth(_sculpt, width);
        const float gap = 4f;
        var modeWidth = MathF.Max(0f, MathF.Floor((width - gap * 3f) / 4f));
        for (var index = 0; index < _modeButtons.Length; index++)
        {
            SetWidth(_modeButtons[index], modeWidth);
            _modeButtons[index].Margin = new Thickness(
                index * (modeWidth + gap), 72f, 0f, 0f);
        }
        var fieldWidth = MathF.Max(0f, width - 82f);
        SetWidth(_radius, fieldWidth);
        SetWidth(_strength, fieldWidth);
        var actionWidth = MathF.Max(0f, MathF.Floor((width - gap * 3f) / 4f));
        for (var index = 0; index < _actionButtons.Length; index++)
        {
            SetWidth(_actionButtons[index], actionWidth);
            _actionButtons[index].Margin = new Thickness(
                index * (actionWidth + gap), 190f, 0f, 0f);
        }
    }

    /// <summary>Synchronizes all controls from current document and tool state.</summary>
    private void RefreshValues()
    {
        _refreshing = true;
        try
        {
            _dimensions.Text = $"{_document.Value.Width} × {_document.Value.Depth} samples";
            _sculpt.IsChecked = _settings.IsEnabled;
            _radius.Text = Format(_settings.Radius);
            _strength.Text = Format(_settings.Strength);
            for (var index = 0; index < _modeButtons.Length; index++)
                _modeButtons[index].IsChecked = (TerrainBrushMode)index == _settings.Mode;
            _undo.IsEnabled = _document.IsEditable && _document.CanUndo;
            _redo.IsEnabled = _document.IsEditable && _document.CanRedo;
            _save.IsEnabled = _document.IsEditable && _document.IsDirty &&
                !_document.IsStrokeActive;
            _reload.IsEnabled = !_document.IsStrokeActive;
            _status.Text = _document.LastError?.Message ??
                (!_document.IsEditable ? "Read-only imported terrain" :
                    _document.IsStrokeActive ? "Sculpting…" :
                    _document.IsDirty ? "Unsaved changes" :
                    _settings.IsEnabled ? "Drag primary pointer over selected terrain" :
                    "Enable Sculpt in Scene to edit heights");
            _status.ForegroundColor = _document.LastError is null
                ? _theme.TextMuted : _theme.Error;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Creates one positive finite brush scalar editor.</summary>
    /// <param name="name">Stable control name.</param>
    /// <param name="y">Row position.</param>
    /// <param name="value">Initial value.</param>
    /// <param name="apply">Validated value writer.</param>
    /// <returns>Configured text field.</returns>
    private TextField CreateFloatField(string name, float y, float value, Action<float> apply)
    {
        var field = new TextField(MathF.Max(0f, Width - 82f), 30f, _theme)
        {
            Name = name,
            Text = Format(value),
            IsReadOnly = !_document.IsEditable,
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = ValidatePositive,
            Margin = new Thickness(82f, y, 0f, 0f)
        };
        if (_document.IsEditable)
        {
            field.ValueUpdateRequested += text =>
            {
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var parsed) && float.IsFinite(parsed) && parsed > 0f)
                    apply(parsed);
                else
                    RefreshValues();
            };
        }
        return field;
    }

    /// <summary>Creates a compact property label.</summary>
    /// <param name="text">Displayed label.</param>
    /// <param name="y">Row position.</param>
    /// <returns>Configured label.</returns>
    private Label CreateLabel(string text, float y)
    {
        return new Label(text, 76f, 30f)
        {
            ForegroundColor = _theme.TextSecondary,
            PaddingLeft = 0f,
            Margin = new Thickness(0f, y, 0f, 0f)
        };
    }

    /// <summary>Validates a positive finite invariant scalar.</summary>
    /// <param name="text">Candidate field text.</param>
    /// <returns>Error text, or null when valid.</returns>
    private static string? ValidatePositive(string text)
    {
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                   out var value) && float.IsFinite(value) && value > 0f
            ? null : "Enter a positive number.";
    }

    /// <summary>Formats one compact invariant scalar.</summary>
    /// <param name="value">Value to format.</param>
    /// <returns>Compact text.</returns>
    private static string Format(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Updates an element width only when required.</summary>
    /// <param name="element">Element to resize.</param>
    /// <param name="width">New nonnegative width.</param>
    private static void SetWidth(UIElement element, float width)
    {
        if (element.Width != width)
            element.Width = width;
    }
}
