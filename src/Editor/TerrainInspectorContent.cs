using System.Globalization;
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Creates the shared terrain asset editor used by file and scene Inspectors.</summary>
public sealed class TerrainInspectorFactory : IAssetInspectorFactory
{
    private readonly TerrainBrushSettings _settings;
    private readonly Func<bool> _undoObjects;
    private readonly Func<bool> _redoObjects;
    private readonly Func<bool> _canUndoObjects;
    private readonly Func<bool> _canRedoObjects;

    /// <summary>Creates a factory over the Scene viewport's shared brush settings.</summary>
    /// <param name="settings">Shared terrain-tool state.</param>
    /// <param name="undoObjects">Object-stroke undo command.</param>
    /// <param name="redoObjects">Object-stroke redo command.</param>
    /// <param name="canUndoObjects">Object-stroke undo availability.</param>
    /// <param name="canRedoObjects">Object-stroke redo availability.</param>
    public TerrainInspectorFactory(
        TerrainBrushSettings settings,
        Func<bool>? undoObjects = null,
        Func<bool>? redoObjects = null,
        Func<bool>? canUndoObjects = null,
        Func<bool>? canRedoObjects = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _undoObjects = undoObjects ?? (static () => false);
        _redoObjects = redoObjects ?? (static () => false);
        _canUndoObjects = canUndoObjects ?? (static () => false);
        _canRedoObjects = canRedoObjects ?? (static () => false);
    }

    /// <inheritdoc/>
    public string ContentType => "nico/terrain";

    /// <inheritdoc/>
    public UIElement Create(IAssetDocument document, AssetInspectorContext context)
    {
        if (document is not TerrainDocument terrain)
            throw new InvalidOperationException("Terrain editor received an invalid document.");
        return new TerrainInspectorContent(context.Width, terrain, _settings,
            displayName: context.ResolveDisplayName,
            undoObjects: _undoObjects,
            redoObjects: _redoObjects,
            canUndoObjects: _canUndoObjects,
            canRedoObjects: _canRedoObjects);
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
    private readonly ToggleButton _resizeSamples;
    private readonly ToggleButton _increaseSamples;
    private readonly ToggleButton _decreaseSamples;
    private readonly TextField _radius;
    private readonly TextField _strength;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _save;
    private readonly Button _reload;
    private readonly Label _dimensions;
    private readonly Label _status;
    private readonly Func<AssetReference, string> _displayName;
    private readonly Func<bool> _canUndoObjects;
    private readonly Func<bool> _canRedoObjects;
    private readonly ToggleButton _paintObjects;
    private readonly AssetReferenceField _objectMesh;
    private readonly Button _clearObjectMesh;
    private readonly ToggleButton _placeObjects;
    private readonly ToggleButton _eraseObjects;
    private readonly TextField _objectSpacing;
    private readonly TextField _objectDensity;
    private readonly TextField _minimumObjectScale;
    private readonly TextField _maximumObjectScale;
    private readonly ToggleButton _alignObjects;
    private readonly ToggleButton _randomizeYaw;
    private readonly Button _undoObjects;
    private readonly Button _redoObjects;
    private readonly Label _objectStatus;
    private bool _refreshing;
    private bool _active;

    /// <summary>Creates terrain document and Scene brush controls.</summary>
    /// <param name="width">Available Inspector width.</param>
    /// <param name="document">Shared terrain document.</param>
    /// <param name="settings">Shared Scene brush settings.</param>
    /// <param name="theme">Theme supplying visuals.</param>
    /// <param name="displayName">Optional painted-mesh display-name resolver.</param>
    /// <param name="undoObjects">Optional object-stroke undo command.</param>
    /// <param name="redoObjects">Optional object-stroke redo command.</param>
    /// <param name="canUndoObjects">Optional object-stroke undo availability.</param>
    /// <param name="canRedoObjects">Optional object-stroke redo availability.</param>
    public TerrainInspectorContent(
        float width,
        TerrainDocument document,
        TerrainBrushSettings settings,
        UITheme? theme = null,
        Func<AssetReference, string>? displayName = null,
        Func<bool>? undoObjects = null,
        Func<bool>? redoObjects = null,
        Func<bool>? canUndoObjects = null,
        Func<bool>? canRedoObjects = null)
        : base(new Color(0f, 0f, 0f), width, 694f)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _theme = theme ?? UITheme.Dark;
        _displayName = displayName ?? (static reference => reference.ToString());
        _canUndoObjects = canUndoObjects ?? (static () => false);
        _canRedoObjects = canRedoObjects ?? (static () => false);
        PaintBackground = false;

        _dimensions = new Label(string.Empty, width, 26f)
        {
            Name = "TerrainDimensions",
            ForegroundColor = _theme.TextSecondary,
            Padding = Thickness.Zero
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
            {
                _settings.ToolMode = TerrainToolMode.Sculpt;
                _settings.IsEnabled = value;
            }
        };
        AddChild(_sculpt);

        _resizeSamples = new ToggleButton(width, 30f, "Resize Samples", _theme,
            ButtonStyle.Primary)
        {
            Name = "TerrainSampleResizeEnabled",
            IsEnabled = document.IsEditable,
            Margin = new Thickness(0f, 34f, 0f, 0f)
        };
        _resizeSamples.CheckedChanged += value =>
        {
            if (!_refreshing)
            {
                _settings.ToolMode = TerrainToolMode.Samples;
                _settings.IsEnabled = value;
            }
        };
        AddChild(_resizeSamples);

        _increaseSamples = new ToggleButton(0f, 30f, "Increase", _theme)
        {
            Name = "TerrainSamplesIncrease",
            IsEnabled = document.IsEditable,
            Margin = new Thickness(0f, 72f, 0f, 0f)
        };
        _decreaseSamples = new ToggleButton(0f, 30f, "Decrease", _theme)
        {
            Name = "TerrainSamplesDecrease",
            IsEnabled = document.IsEditable,
            Margin = new Thickness(0f, 72f, 0f, 0f)
        };
        _increaseSamples.CheckedChanged += value =>
        {
            if (!_refreshing && value)
                _settings.IncreaseSamples = true;
        };
        _decreaseSamples.CheckedChanged += value =>
        {
            if (!_refreshing && value)
                _settings.IncreaseSamples = false;
        };
        AddChild(_increaseSamples);
        AddChild(_decreaseSamples);

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
            Padding = Thickness.Zero,
            Margin = new Thickness(0f, 232f, 0f, 0f)
        };
        AddChild(_status);

        _paintObjects = new ToggleButton(width, 30f, "Paint Objects in Scene", _theme,
            ButtonStyle.Primary)
        {
            Name = "TerrainObjectPaintEnabled",
            IsEnabled = true,
            Margin = new Thickness(0f, 304f, 0f, 0f)
        };
        _paintObjects.CheckedChanged += value =>
        {
            if (_refreshing)
                return;
            _settings.ToolMode = TerrainToolMode.Objects;
            _settings.IsEnabled = value;
        };
        AddChild(_paintObjects);

        _objectMesh = new AssetReferenceField(MathF.Max(0f, width - 62f), 30f,
            "nico/static-mesh", reference =>
            {
                _settings.ObjectMesh = reference;
                return true;
            }, _theme)
        {
            Name = "TerrainObjectMesh",
            Placeholder = "Drop a static mesh",
            IsEnabled = true,
            Margin = new Thickness(0f, 342f, 0f, 0f)
        };
        AddChild(_objectMesh);
        _clearObjectMesh = new Button(58f, 30f, "Clear", _theme)
        {
            Name = "TerrainObjectMeshClear",
            IsEnabled = true,
            Margin = new Thickness(MathF.Max(0f, width - 58f), 342f, 0f, 0f)
        };
        _clearObjectMesh.Click += () => _settings.ObjectMesh = null;
        AddChild(_clearObjectMesh);

        _placeObjects = new ToggleButton(0f, 30f, "Place", _theme)
        {
            Name = "TerrainObjectPlace",
            IsEnabled = true,
            Margin = new Thickness(0f, 380f, 0f, 0f)
        };
        _eraseObjects = new ToggleButton(0f, 30f, "Erase", _theme)
        {
            Name = "TerrainObjectErase",
            IsEnabled = true,
            Margin = new Thickness(0f, 380f, 0f, 0f)
        };
        _placeObjects.CheckedChanged += value =>
        {
            if (!_refreshing && value)
                _settings.EraseObjects = false;
        };
        _eraseObjects.CheckedChanged += value =>
        {
            if (!_refreshing && value)
                _settings.EraseObjects = true;
        };
        AddChild(_placeObjects);
        AddChild(_eraseObjects);

        AddChild(CreateLabel("Spacing", 418f));
        _objectSpacing = CreateFloatField("TerrainObjectSpacing", 418f,
            _settings.ObjectSpacing, value => _settings.ObjectSpacing = value,
            requiresEditableDocument: false);
        AddChild(_objectSpacing);
        AddChild(CreateLabel("Density", 456f));
        _objectDensity = CreateFloatField("TerrainObjectDensity", 456f,
            _settings.ObjectDensity,
            value => _settings.ObjectDensity = Math.Clamp(value, 0.01f, 1f),
            requiresEditableDocument: false);
        AddChild(_objectDensity);
        AddChild(CreateLabel("Min Scale", 494f));
        _minimumObjectScale = CreateFloatField("TerrainObjectMinimumScale", 494f,
            _settings.MinimumObjectScale, value => _settings.MinimumObjectScale = value,
            requiresEditableDocument: false);
        AddChild(_minimumObjectScale);
        AddChild(CreateLabel("Max Scale", 532f));
        _maximumObjectScale = CreateFloatField("TerrainObjectMaximumScale", 532f,
            _settings.MaximumObjectScale, value => _settings.MaximumObjectScale = value,
            requiresEditableDocument: false);
        AddChild(_maximumObjectScale);

        _alignObjects = new ToggleButton(0f, 30f, "Align Normal", _theme)
        {
            Name = "TerrainObjectAlignNormal",
            IsEnabled = true,
            Margin = new Thickness(0f, 570f, 0f, 0f)
        };
        _randomizeYaw = new ToggleButton(0f, 30f, "Random Yaw", _theme)
        {
            Name = "TerrainObjectRandomYaw",
            IsEnabled = true,
            Margin = new Thickness(0f, 570f, 0f, 0f)
        };
        _alignObjects.CheckedChanged += value =>
        {
            if (!_refreshing)
                _settings.AlignObjectsToNormal = value;
        };
        _randomizeYaw.CheckedChanged += value =>
        {
            if (!_refreshing)
                _settings.RandomizeObjectYaw = value;
        };
        AddChild(_alignObjects);
        AddChild(_randomizeYaw);

        _undoObjects = new Button(0f, 30f, "Undo Objects", _theme)
            { Name = "TerrainObjectUndo", Margin = new Thickness(0f, 608f, 0f, 0f) };
        _redoObjects = new Button(0f, 30f, "Redo Objects", _theme)
            { Name = "TerrainObjectRedo", Margin = new Thickness(0f, 608f, 0f, 0f) };
        var undoObjectCommand = undoObjects ?? (static () => false);
        var redoObjectCommand = redoObjects ?? (static () => false);
        _undoObjects.Click += () =>
        {
            if (undoObjectCommand())
                RefreshValues();
        };
        _redoObjects.Click += () =>
        {
            if (redoObjectCommand())
                RefreshValues();
        };
        AddChild(_undoObjects);
        AddChild(_redoObjects);

        _objectStatus = new Label(string.Empty, width, 42f)
        {
            Name = "TerrainObjectStatus",
            ForegroundColor = _theme.TextMuted,
            Padding = Thickness.Zero,
            Margin = new Thickness(0f, 646f, 0f, 0f)
        };
        AddChild(_objectStatus);
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
        SetWidth(_objectStatus, width);
        SetWidth(_sculpt, width);
        const float gap = 4f;
        var primaryWidth = MathF.Max(0f, MathF.Floor((width - gap) * 0.5f));
        SetWidth(_sculpt, primaryWidth);
        SetWidth(_resizeSamples, primaryWidth);
        _resizeSamples.Margin = new Thickness(primaryWidth + gap, 34f, 0f, 0f);
        SetWidth(_paintObjects, width);
        var modeWidth = MathF.Max(0f, MathF.Floor((width - gap * 3f) / 4f));
        for (var index = 0; index < _modeButtons.Length; index++)
        {
            SetWidth(_modeButtons[index], modeWidth);
            _modeButtons[index].Margin = new Thickness(
                index * (modeWidth + gap), 72f, 0f, 0f);
        }
        SetWidth(_increaseSamples, primaryWidth);
        SetWidth(_decreaseSamples, primaryWidth);
        _decreaseSamples.Margin = new Thickness(primaryWidth + gap, 72f, 0f, 0f);
        var fieldWidth = MathF.Max(0f, width - 82f);
        SetWidth(_radius, fieldWidth);
        SetWidth(_strength, fieldWidth);
        SetWidth(_objectSpacing, fieldWidth);
        SetWidth(_objectDensity, fieldWidth);
        SetWidth(_minimumObjectScale, fieldWidth);
        SetWidth(_maximumObjectScale, fieldWidth);
        var actionWidth = MathF.Max(0f, MathF.Floor((width - gap * 3f) / 4f));
        for (var index = 0; index < _actionButtons.Length; index++)
        {
            SetWidth(_actionButtons[index], actionWidth);
            _actionButtons[index].Margin = new Thickness(
                index * (actionWidth + gap), 190f, 0f, 0f);
        }
        SetWidth(_objectMesh, MathF.Max(0f, width - 62f));
        _clearObjectMesh.Margin = new Thickness(MathF.Max(0f, width - 58f), 342f, 0f, 0f);
        var halfWidth = MathF.Max(0f, MathF.Floor((width - gap) * 0.5f));
        SetWidth(_placeObjects, halfWidth);
        SetWidth(_eraseObjects, halfWidth);
        _eraseObjects.Margin = new Thickness(halfWidth + gap, 380f, 0f, 0f);
        SetWidth(_alignObjects, halfWidth);
        SetWidth(_randomizeYaw, halfWidth);
        _randomizeYaw.Margin = new Thickness(halfWidth + gap, 570f, 0f, 0f);
        SetWidth(_undoObjects, halfWidth);
        SetWidth(_redoObjects, halfWidth);
        _redoObjects.Margin = new Thickness(halfWidth + gap, 608f, 0f, 0f);
    }

    /// <summary>Synchronizes all controls from current document and tool state.</summary>
    private void RefreshValues()
    {
        _refreshing = true;
        try
        {
            _dimensions.Text = $"{_document.Value.Width} × {_document.Value.Depth} base • " +
                $"{_document.Value.GetActiveSamples().Length} active";
            _sculpt.IsChecked = _settings.IsEnabled &&
                _settings.ToolMode == TerrainToolMode.Sculpt;
            _resizeSamples.IsChecked = _settings.IsEnabled &&
                _settings.ToolMode == TerrainToolMode.Samples;
            _increaseSamples.IsChecked = _settings.IncreaseSamples;
            _decreaseSamples.IsChecked = !_settings.IncreaseSamples;
            var resizingSamples = _settings.ToolMode == TerrainToolMode.Samples;
            _increaseSamples.IsVisible = resizingSamples;
            _decreaseSamples.IsVisible = resizingSamples;
            _paintObjects.IsChecked = _settings.IsEnabled &&
                _settings.ToolMode == TerrainToolMode.Objects;
            _radius.Text = Format(_settings.Radius);
            _strength.Text = Format(_settings.Strength);
            _objectMesh.Text = _settings.ObjectMesh is { } mesh
                ? _displayName(mesh) : string.Empty;
            _placeObjects.IsChecked = !_settings.EraseObjects;
            _eraseObjects.IsChecked = _settings.EraseObjects;
            _objectSpacing.Text = Format(_settings.ObjectSpacing);
            _objectDensity.Text = Format(_settings.ObjectDensity);
            _minimumObjectScale.Text = Format(_settings.MinimumObjectScale);
            _maximumObjectScale.Text = Format(_settings.MaximumObjectScale);
            _alignObjects.IsChecked = _settings.AlignObjectsToNormal;
            _randomizeYaw.IsChecked = _settings.RandomizeObjectYaw;
            _undoObjects.IsEnabled = _canUndoObjects();
            _redoObjects.IsEnabled = _canRedoObjects();
            for (var index = 0; index < _modeButtons.Length; index++)
            {
                _modeButtons[index].IsChecked = (TerrainBrushMode)index == _settings.Mode;
                _modeButtons[index].IsVisible = !resizingSamples;
            }
            _undo.IsEnabled = _document.IsEditable && _document.CanUndo;
            _redo.IsEnabled = _document.IsEditable && _document.CanRedo;
            _save.IsEnabled = _document.IsEditable && _document.IsDirty &&
                !_document.IsStrokeActive;
            _reload.IsEnabled = !_document.IsStrokeActive;
            _status.Text = _document.LastError?.Message ??
                (!_document.IsEditable ? "Read-only imported terrain" :
                    _document.IsStrokeActive ? "Sculpting…" :
                    _document.IsDirty ? "Unsaved changes" :
                    _settings.IsEnabled && _settings.ToolMode == TerrainToolMode.Samples
                        ? _settings.IncreaseSamples
                            ? "Drag to add local half-cell terrain samples"
                            : "Drag to remove local half-cell terrain samples" :
                    _settings.IsEnabled && _settings.ToolMode == TerrainToolMode.Sculpt
                        ? "Drag primary pointer over selected terrain" :
                    "Enable Sculpt in Scene to edit heights");
            _status.ForegroundColor = _document.LastError is null
                ? _theme.TextMuted : _theme.Error;
            _objectStatus.Text = _settings.ObjectMesh is null && !_settings.EraseObjects
                    ? "Drop a static mesh before placing objects"
                    : _settings.IsEnabled && _settings.ToolMode == TerrainToolMode.Objects
                        ? "Drag over selected terrain; switch to Erase to remove"
                        : "Enable Paint Objects in Scene to scatter meshes";
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
    /// <param name="requiresEditableDocument">Whether a read-only terrain disables the field.</param>
    /// <returns>Configured text field.</returns>
    private TextField CreateFloatField(
        string name,
        float y,
        float value,
        Action<float> apply,
        bool requiresEditableDocument = true)
    {
        var enabled = !requiresEditableDocument || _document.IsEditable;
        var field = new TextField(MathF.Max(0f, Width - 82f), 30f, _theme)
        {
            Name = name,
            Text = Format(value),
            IsReadOnly = !enabled,
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = ValidatePositive,
            Margin = new Thickness(82f, y, 0f, 0f)
        };
        if (enabled)
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
            Padding = Thickness.Zero,
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
