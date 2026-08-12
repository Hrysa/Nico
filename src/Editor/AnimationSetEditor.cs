using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Edits one human-readable animation-set source asset.</summary>
public sealed class AnimationSetEditor : ContentControl
{
    private readonly List<AnimationSetEntry> _entries = [];
    private readonly ListView _entryList;
    private readonly TextField _aliasField;
    private readonly TextField _sourceField;
    private readonly TextField _clipField;
    private readonly NumericField _speedField;
    private readonly CheckBox _loopField;
    private readonly CheckBox _inPlaceField;
    private readonly TextField _rootMotionJointField;
    private readonly Label _pathLabel;
    private readonly Label _statusLabel;
    private bool _synchronizing;
    private string? _path;

    /// <summary>Occurs after the current source asset is saved successfully.</summary>
    public event Action<string>? Saved;

    /// <summary>Gets the currently opened source path, or null.</summary>
    public string? Path => _path;

    /// <summary>Gets the editable entries in authored order.</summary>
    public IReadOnlyList<AnimationSetEntry> Entries => _entries;

    /// <summary>Creates an empty animation-set editor.</summary>
    /// <param name="theme">Theme supplying editor visuals.</param>
    public AnimationSetEditor(UITheme? theme = null)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        Name = "AnimationSetEditor";
        AllowDrop = true;
        Drag += OnDrag;

        _pathLabel = new Label("Open a .nanimset asset", 0f, resolvedTheme.ControlHeight)
        {
            Name = "AnimationSetPath",
            ForegroundColor = resolvedTheme.TextSecondary,
            PaddingLeft = 6f
        };
        _statusLabel = new Label("Drop imported animations here to add clips", 0f,
            resolvedTheme.ControlHeight)
        {
            Name = "AnimationSetStatus",
            ForegroundColor = resolvedTheme.TextSecondary,
            PaddingLeft = 6f
        };
        _entryList = new ListView(220f, 0f, resolvedTheme)
        {
            Name = "AnimationSetEntries",
            Width = 220f,
            Height = 0f,
            FlexShrink = 0f
        };
        _aliasField = CreateField("AnimationAlias", resolvedTheme);
        _sourceField = CreateField("AnimationSource", resolvedTheme);
        _sourceField.IsReadOnly = true;
        _clipField = CreateField("AnimationClip", resolvedTheme);
        _speedField = new NumericField(0f, resolvedTheme.ControlHeight, resolvedTheme)
        {
            Name = "AnimationSpeed",
            Width = 0f,
            Minimum = -100d,
            Maximum = 100d,
            Step = 0.1d,
            FormatString = "0.###"
        };
        _loopField = new CheckBox(120f, resolvedTheme.ControlHeight,
            "Loop", resolvedTheme) { Name = "AnimationLoop" };
        _inPlaceField = new CheckBox(180f, resolvedTheme.ControlHeight,
            "Remove horizontal root motion", resolvedTheme)
        {
            Name = "AnimationInPlace"
        };
        _rootMotionJointField = CreateField("AnimationRootMotionJoint", resolvedTheme);

        var save = new Button(76f, resolvedTheme.ControlHeight, "Save", resolvedTheme,
            ButtonStyle.Primary) { Name = "AnimationSetSave" };
        var reload = new Button(76f, resolvedTheme.ControlHeight, "Reload", resolvedTheme)
            { Name = "AnimationSetReload" };
        var remove = new Button(86f, resolvedTheme.ControlHeight, "Remove", resolvedTheme)
            { Name = "AnimationSetRemove" };
        save.Click += Save;
        reload.Click += Reload;
        remove.Click += RemoveSelected;
        _entryList.SelectionChanged += SelectEntry;
        _aliasField.TextChanged += value => UpdateSelected(entry => entry with { Alias = value });
        _clipField.TextChanged += value => UpdateSelected(entry => entry with
        {
            Clip = string.IsNullOrWhiteSpace(value) ? null : value
        });
        _speedField.ValueChanged += value => UpdateSelected(entry => entry with
        {
            Speed = (float)value
        });
        _loopField.CheckedChanged += value => UpdateSelected(entry => entry with
        {
            Loop = value
        });
        _inPlaceField.CheckedChanged += value => UpdateSelected(entry => entry with
        {
            InPlace = value,
            RootMotionJoint = value && !string.IsNullOrWhiteSpace(_rootMotionJointField.Text)
                ? _rootMotionJointField.Text : null
        });
        _rootMotionJointField.TextChanged += value => UpdateSelected(entry => entry with
        {
            RootMotionJoint = entry.InPlace && !string.IsNullOrWhiteSpace(value) ? value : null
        });

        var form = UI.Column(
        [
            FormRow("Alias", _aliasField, resolvedTheme),
            FormRow("Source", _sourceField, resolvedTheme),
            FormRow("Source clip", _clipField, resolvedTheme),
            FormRow("Speed", _speedField, resolvedTheme),
            _loopField,
            _inPlaceField,
            FormRow("Root joint", _rootMotionJointField, resolvedTheme),
            new FlexPanel().Grow()
        ], backgroundColor: resolvedTheme.Surface).Configure(panel =>
        {
            panel.Padding = new Thickness(12f);
            panel.Gap = 8f;
        }).Grow();
        Content = UI.Column(
        [
            UI.Row([save, reload, remove, _pathLabel.Grow()],
                backgroundColor: resolvedTheme.SurfaceRaised,
                alignItems: FlexAlignment.Center, gap: 6f).Configure(toolbar =>
                {
                    toolbar.Height = resolvedTheme.ControlHeight + 8f;
                    toolbar.Padding = new Thickness(6f, 4f);
                    toolbar.FlexShrink = 0f;
                }),
            UI.Row([_entryList, form], backgroundColor: resolvedTheme.Surface).Grow(),
            _statusLabel.Configure(status => status.FlexShrink = 0f)
        ], backgroundColor: resolvedTheme.Surface);
        SetEditorEnabled(false);
    }

    /// <summary>Loads an animation-set source for editing.</summary>
    /// <param name="path">Readable `.nanimset` path.</param>
    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        var resource = AnimationSetResource.Load(stream);
        _path = fullPath;
        _entries.Clear();
        for (var index = 0; index < resource.Entries.Count; index++)
            _entries.Add(resource.Entries[index]);
        _pathLabel.Text = System.IO.Path.GetFileName(fullPath);
        _statusLabel.Text = $"{_entries.Count} animation clip(s)";
        RefreshList(selectIndex: _entries.Count > 0 ? 0 : -1);
        SetEditorEnabled(true);
    }

    /// <summary>Adds an imported animation artifact and selects its new entry.</summary>
    /// <param name="source">Imported skeletal-animation artifact.</param>
    /// <returns>True when the artifact was accepted.</returns>
    public bool Add(ImportedSubAssetNode source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_path is null || source.ContentType != "nico/skeletal-animation")
            return false;
        var alias = CreateUniqueAlias(GetDefaultAlias(source));
        _entries.Add(new AnimationSetEntry(alias, source.Reference));
        RefreshList(_entries.Count - 1);
        _statusLabel.Text = $"Added {alias}; save to publish changes";
        return true;
    }

    /// <summary>Saves the current entries as readable source JSON.</summary>
    public void Save()
    {
        if (_path is null)
            return;
        try
        {
            AnimationSetAuthoring.Save(_path, _entries.ToArray());
            _statusLabel.Text = $"Saved {System.IO.Path.GetFileName(_path)}";
            Saved?.Invoke(_path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                           UnauthorizedAccessException)
        {
            _statusLabel.Text = exception.Message;
        }
    }

    /// <summary>Reloads the currently opened source from disk.</summary>
    public void Reload()
    {
        if (_path is not null)
            Open(_path);
    }

    /// <summary>Creates a standard form row.</summary>
    /// <param name="label">Displayed field label.</param>
    /// <param name="field">Editable field.</param>
    /// <param name="theme">Resolved UI theme.</param>
    /// <returns>Configured horizontal form row.</returns>
    private static FlexPanel FormRow(string label, TextField field, UITheme theme) => UI.Row(
    [
        new Label(label, 92f, theme.ControlHeight)
        {
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 0f,
            FlexShrink = 0f
        },
        field.Grow()
    ], alignItems: FlexAlignment.Center, gap: 8f);

    /// <summary>Creates a numeric form row.</summary>
    /// <param name="label">Displayed field label.</param>
    /// <param name="field">Editable numeric field.</param>
    /// <param name="theme">Resolved UI theme.</param>
    /// <returns>Configured horizontal form row.</returns>
    private static FlexPanel FormRow(string label, NumericField field, UITheme theme) => UI.Row(
    [
        new Label(label, 92f, theme.ControlHeight)
        {
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 0f,
            FlexShrink = 0f
        },
        field.Grow()
    ], alignItems: FlexAlignment.Center, gap: 8f);

    /// <summary>Creates one editor text field.</summary>
    /// <param name="name">Stable element name.</param>
    /// <param name="theme">Resolved UI theme.</param>
    /// <returns>Configured field.</returns>
    private static TextField CreateField(string name, UITheme theme) =>
        new(0f, theme.ControlHeight, theme) { Name = name, Width = 0f };

    /// <summary>Removes the currently selected authored entry.</summary>
    private void RemoveSelected()
    {
        var index = _entryList.SelectedIndex;
        if ((uint)index >= (uint)_entries.Count)
            return;
        _entries.RemoveAt(index);
        RefreshList(Math.Min(index, _entries.Count - 1));
        _statusLabel.Text = "Entry removed; save to publish changes";
    }

    /// <summary>Synchronizes entry detail fields with list selection.</summary>
    /// <param name="index">Selected entry index.</param>
    /// <param name="text">Selected display text.</param>
    private void SelectEntry(int index, string? text)
    {
        _synchronizing = true;
        if ((uint)index < (uint)_entries.Count)
        {
            var entry = _entries[index];
            _aliasField.Text = entry.Alias;
            _sourceField.Text = entry.Source.ToString();
            _clipField.Text = entry.Clip ?? string.Empty;
            _speedField.Value = entry.Speed;
            _loopField.IsChecked = entry.Loop;
            _inPlaceField.IsChecked = entry.InPlace;
            _rootMotionJointField.Text = entry.RootMotionJoint ?? string.Empty;
            _rootMotionJointField.IsEnabled = entry.InPlace;
        }
        else
        {
            _aliasField.Text = string.Empty;
            _sourceField.Text = string.Empty;
            _clipField.Text = string.Empty;
            _speedField.Value = 1d;
            _loopField.IsChecked = true;
            _inPlaceField.IsChecked = false;
            _rootMotionJointField.Text = string.Empty;
            _rootMotionJointField.IsEnabled = false;
        }
        _synchronizing = false;
    }

    /// <summary>Applies one immutable-entry edit to the current selection.</summary>
    /// <param name="update">Entry transformation.</param>
    private void UpdateSelected(Func<AnimationSetEntry, AnimationSetEntry> update)
    {
        if (_synchronizing)
            return;
        var index = _entryList.SelectedIndex;
        if ((uint)index >= (uint)_entries.Count)
            return;
        _entries[index] = update(_entries[index]);
        _rootMotionJointField.IsEnabled = _entries[index].InPlace;
        RefreshList(index);
        _statusLabel.Text = "Modified; save to publish changes";
    }

    /// <summary>Rebuilds list labels while retaining a requested selection.</summary>
    /// <param name="selectIndex">Entry index to select afterward.</param>
    private void RefreshList(int selectIndex)
    {
        var labels = new string[_entries.Count];
        for (var index = 0; index < labels.Length; index++)
            labels[index] = _entries[index].Alias;
        _entryList.SetItems(labels);
        _entryList.Select(selectIndex);
    }

    /// <summary>Enables or disables fields that require an open document.</summary>
    /// <param name="enabled">Whether a document is open.</param>
    private void SetEditorEnabled(bool enabled)
    {
        _entryList.IsEnabled = enabled;
        _aliasField.IsEnabled = enabled;
        _clipField.IsEnabled = enabled;
        _speedField.IsEnabled = enabled;
        _loopField.IsEnabled = enabled;
        _inPlaceField.IsEnabled = enabled;
        _rootMotionJointField.IsEnabled = enabled && _inPlaceField.IsChecked;
    }

    /// <summary>Accepts imported animation artifacts dropped anywhere on the editor.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="dragEvent">Routed drag event.</param>
    private void OnDrag(UIElement sender, UIDragEventArgs dragEvent)
    {
        if (dragEvent.RoutePhase == UIRoutePhase.Preview || _path is null ||
            !dragEvent.Data.TryGet<EditorTreeDragData>(out var payload) ||
            payload?.Item is not ImportedSubAssetNode
            {
                ContentType: "nico/skeletal-animation"
            } source)
            return;
        if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
            dragEvent.Effect = UIDragEffect.Copy;
        else if (dragEvent.Kind == UIDragEventKind.Drop && Add(source))
            dragEvent.Effect = UIDragEffect.Copy;
        dragEvent.Handled = true;
    }

    /// <summary>Builds a concise alias from an imported artifact display name.</summary>
    /// <param name="source">Imported animation artifact.</param>
    /// <returns>Non-empty initial alias.</returns>
    private static string GetDefaultAlias(ImportedSubAssetNode source)
    {
        var marker = source.Name.LastIndexOf(" [", StringComparison.Ordinal);
        var alias = marker > 0 ? source.Name[..marker] : source.Name;
        return string.IsNullOrWhiteSpace(alias) ? "Animation" : alias;
    }

    /// <summary>Creates an ordinally unique alias within the current set.</summary>
    /// <param name="preferred">Preferred base alias.</param>
    /// <returns>Available alias.</returns>
    private string CreateUniqueAlias(string preferred)
    {
        var candidate = preferred;
        var suffix = 2;
        while (ContainsAlias(candidate))
            candidate = $"{preferred} {suffix++}";
        return candidate;
    }

    /// <summary>Checks existing aliases using runtime identity rules.</summary>
    /// <param name="alias">Alias candidate.</param>
    /// <returns>True when already present.</returns>
    private bool ContainsAlias(string alias)
    {
        for (var index = 0; index < _entries.Count; index++)
        {
            if (string.Equals(_entries[index].Alias, alias, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
