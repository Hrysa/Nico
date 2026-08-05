using System.Globalization;
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>
/// Displays and edits properties of the selected authored scene node.
/// </summary>
public sealed class SceneInspector : Panel
{
    private readonly UITheme _theme;
    private readonly List<Func<bool>> _refreshBindings = new();
    private readonly Dictionary<Node, CachedInspectorView> _cachedViews = new();
    private MaterialProperties? _resolvedMaterial;

    /// <summary>Gets or sets the editor display-name resolver for attached script assets.</summary>
    public Func<AssetId, string?>? ResolveScriptName { get; set; }

    /// <summary>Gets or sets the resolver for a mesh instance's effective material values.</summary>
    public Func<MeshInstance3D, MaterialProperties>? ResolveMaterial { get; set; }

    /// <summary>Gets or sets the display-name resolver for a mesh material assignment.</summary>
    public Func<MeshInstance3D, string>? ResolveMaterialName { get; set; }

    /// <summary>Gets the node currently displayed by the Inspector.</summary>
    public Node? InspectedNode { get; private set; }

    /// <summary>Occurs after an Inspector field changes the selected node.</summary>
    public event Action<Node>? NodeChanged;

    /// <summary>Occurs after the Inspector changes the selected node's displayed name.</summary>
    public event Action<Node>? NodeNameChanged;

    /// <summary>
    /// Creates an empty scene Inspector.
    /// </summary>
    /// <param name="width">Inspector content width.</param>
    /// <param name="height">Inspector content height.</param>
    /// <param name="theme">Theme supplying Inspector visuals.</param>
    public SceneInspector(float width, float height, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Surface, width, height)
    {
        _theme = theme ?? UITheme.Dark;
        PaintBackground = false;
        Bind(null);
    }

    /// <summary>
    /// Rebuilds Inspector fields for a selected scene node.
    /// </summary>
    /// <param name="node">Selected authored node, or null.</param>
    public void Bind(Node? node)
    {
        if (ReferenceEquals(InspectedNode, node) && Children.Count > 0)
        {
            ResolveBoundMaterial(node);
            RefreshValues();
            return;
        }

        InspectedNode = node;
        ClearChildren();
        _refreshBindings.Clear();
        ResolveBoundMaterial(node);
        if (node is null)
        {
            AddChild(CreateLabel(12f, 12f, Width - 24f, 28f,
                "Select an object to inspect", _theme.TextMuted));
            return;
        }
        if (RestoreCachedView(node))
            return;

        AddChild(CreateLabel(12f, 8f, Width - 24f, 24f,
            node.GetType().Name, _theme.TextSecondary));
        AddChild(CreateLabel(12f, 40f, 58f, 30f, "Name", _theme.TextSecondary));
        var nameField = new TextField(Width - 84f, 30f, _theme)
        {
            Name = "NameField",
            Text = node.Name,
            Margin = new Thickness(72f, 40f, 0f, 0f)
        };
        nameField.TextChanged += value =>
        {
            node.Name = value;
            NodeChanged?.Invoke(node);
            NodeNameChanged?.Invoke(node);
        };
        RegisterRefresh(nameField, () => node.Name);
        AddChild(nameField);

        AddChild(CreateLabel(12f, 82f, Width - 24f, 26f,
            "Transform", _theme.TextPrimary));
        AddVectorRow("Position", "Position", 112f, () => node.Position,
            value => node.Position = value, radiansAsDegrees: false);
        AddVectorRow("Rotation", "Rotation", 150f, () => node.Rotation,
            value => node.Rotation = value, radiansAsDegrees: true);
        AddVectorRow("Scale", "Scale", 188f, () => node.Scale,
            value => node.Scale = value, radiansAsDegrees: false);

        var scriptY = 236f;
        if (node is MeshInstance3D meshInstance)
        {
            AddMaterialSection(meshInstance, 236f);
            scriptY = 464f;
        }

        AddChild(CreateLabel(12f, scriptY, Width - 24f, 26f,
            "Script", _theme.TextPrimary));
        var scriptField = new TextField(Width - 24f, 30f, _theme)
        {
            Name = "ScriptAssetField",
            Text = GetScriptDisplayName(node.ScriptId),
            Placeholder = "No script attached",
            IsReadOnly = true,
            Margin = new Thickness(12f, scriptY + 30f, 0f, 0f)
        };
        RegisterRefresh(scriptField, () => GetScriptDisplayName(node.ScriptId));
        AddChild(scriptField);
        CacheCurrentView(node);
    }

    /// <summary>Resolves material values associated with the currently bound node.</summary>
    /// <param name="node">Node being bound, or null.</param>
    private void ResolveBoundMaterial(Node? node)
    {
        _resolvedMaterial = node is MeshInstance3D boundMeshInstance
            ? boundMeshInstance.MaterialOverride ?? ResolveMaterial?.Invoke(boundMeshInstance)
                ?? MaterialProperties.Default
            : null;
    }

    /// <summary>Restores a previously constructed Inspector view for a node.</summary>
    /// <param name="node">Node whose view should be restored.</param>
    /// <returns>True when a retained view was restored.</returns>
    private bool RestoreCachedView(Node node)
    {
        if (!_cachedViews.TryGetValue(node, out var cached))
            return false;
        foreach (var child in cached.Children)
            AddChild(child);
        _refreshBindings.AddRange(cached.RefreshBindings);
        RefreshValues();
        return true;
    }

    /// <summary>Retains the current Inspector controls and refresh bindings for reuse.</summary>
    /// <param name="node">Node owning the constructed view.</param>
    private void CacheCurrentView(Node node)
    {
        _cachedViews[node] = new CachedInspectorView(
            Children.OfType<UIElement>().ToArray(),
            _refreshBindings.ToArray());
    }

    /// <summary>Adds slot-zero material assignment and copy-on-write property editors.</summary>
    /// <param name="instance">Inspected mesh instance.</param>
    /// <param name="y">Section top.</param>
    private void AddMaterialSection(MeshInstance3D instance, float y)
    {
        AddChild(CreateLabel(12f, y, Width - 24f, 26f, "Material", _theme.TextPrimary));
        var materialField = new TextField(Width - 88f, 30f, _theme)
        {
            Name = "MaterialSlot0",
            Text = GetMaterialName(instance),
            IsReadOnly = true,
            Margin = new Thickness(12f, y + 30f, 0f, 0f)
        };
        RegisterRefresh(materialField, () => GetMaterialName(instance));
        AddChild(materialField);
        var reset = new Button(60f, 30f, "Reset", _theme)
        {
            Name = "MaterialReset",
            Margin = new Thickness(Width - 72f, y + 30f, 0f, 0f)
        };
        reset.Click += () =>
        {
            instance.MaterialOverride = null;
            NodeChanged?.Invoke(instance);
            Bind(instance);
        };
        AddChild(reset);

        AddMaterialVector4Row(instance, y + 68f);
        AddMaterialFloatRow(instance, "Metallic", "MaterialMetallic", y + 106f,
            material => material.Metallic,
            (material, value) => material.Metallic = Math.Clamp(value, 0f, 1f));
        AddMaterialFloatRow(instance, "Roughness", "MaterialRoughness", y + 144f,
            material => material.Roughness,
            (material, value) => material.Roughness = Math.Clamp(value, 0f, 1f));
        AddChild(CreateLabel(12f, y + 182f, 80f, 30f, "Texture", _theme.TextSecondary));
        var textureField = new TextField(Width - 104f, 30f, _theme)
        {
            Name = "MaterialBaseColorTexture",
            Text = GetEffectiveMaterial(instance).BaseColorTexture?.ToString() ?? string.Empty,
            Placeholder = "No base-color texture",
            IsReadOnly = true,
            Margin = new Thickness(92f, y + 182f, 0f, 0f)
        };
        RegisterRefresh(textureField, () =>
            GetEffectiveMaterial(instance).BaseColorTexture?.ToString() ?? string.Empty);
        AddChild(textureField);
    }

    /// <summary>Adds four editable base-color components.</summary>
    /// <param name="instance">Inspected mesh instance.</param>
    /// <param name="y">Row top.</param>
    private void AddMaterialVector4Row(MeshInstance3D instance, float y)
    {
        const float labelWidth = 66f;
        const float spacing = 4f;
        var fieldWidth = MathF.Floor((MathF.Max(0f, Width - 24f - labelWidth) - spacing * 3f) / 4f);
        AddChild(CreateLabel(12f, y, labelWidth, 30f, "Base Color", _theme.TextSecondary));
        for (var index = 0; index < 4; index++)
        {
            var componentIndex = index;
            var field = new TextField(fieldWidth, 30f, _theme)
            {
                Name = $"MaterialBaseColor{"RGBA"[index]}",
                Text = Format(GetComponent(GetEffectiveMaterial(instance).BaseColor, index)),
                Margin = new Thickness(12f + labelWidth + index * (fieldWidth + spacing), y, 0f, 0f)
            };
            field.TextChanged += text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var component))
                    return;
                var material = GetOrCreateOverride(instance);
                material.BaseColor = WithComponent(material.BaseColor, componentIndex,
                    Math.Clamp(component, 0f, 1f));
            };
            field.Blur += () => NodeChanged?.Invoke(instance);
            RegisterRefresh(field, () => Format(GetComponent(
                GetEffectiveMaterial(instance).BaseColor, componentIndex)));
            AddChild(field);
        }
    }

    /// <summary>Adds one editable scalar material property.</summary>
    /// <param name="instance">Inspected mesh instance.</param>
    /// <param name="label">Property label.</param>
    /// <param name="name">Field element name.</param>
    /// <param name="y">Row top.</param>
    /// <param name="read">Property reader.</param>
    /// <param name="write">Property writer.</param>
    private void AddMaterialFloatRow(
        MeshInstance3D instance,
        string label,
        string name,
        float y,
        Func<MaterialProperties, float> read,
        Action<MaterialProperties, float> write)
    {
        AddChild(CreateLabel(12f, y, 80f, 30f, label, _theme.TextSecondary));
        var field = new TextField(Width - 104f, 30f, _theme)
        {
            Name = name,
            Text = Format(read(GetEffectiveMaterial(instance))),
            Margin = new Thickness(92f, y, 0f, 0f)
        };
        field.TextChanged += text =>
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var value))
                return;
            write(GetOrCreateOverride(instance), value);
        };
        field.Blur += () => NodeChanged?.Invoke(instance);
        RegisterRefresh(field, () => Format(read(GetEffectiveMaterial(instance))));
        AddChild(field);
    }

    /// <summary>Gets the effective material without changing scene ownership.</summary>
    /// <param name="instance">Mesh instance.</param>
    /// <returns>Override, resolved assignment, or shared default values.</returns>
    private MaterialProperties GetEffectiveMaterial(MeshInstance3D instance)
    {
        return instance.MaterialOverride ?? _resolvedMaterial ?? MaterialProperties.Default;
    }

    /// <summary>Creates a scene-local material copy on the first property edit.</summary>
    /// <param name="instance">Mesh instance receiving an override.</param>
    /// <returns>The editable scene-local material.</returns>
    private MaterialProperties GetOrCreateOverride(MeshInstance3D instance)
    {
        return instance.MaterialOverride ??= GetEffectiveMaterial(instance).Clone();
    }

    /// <summary>Formats the current slot-zero material ownership.</summary>
    /// <param name="instance">Mesh instance.</param>
    /// <returns>Material display name.</returns>
    private string GetMaterialName(MeshInstance3D instance)
    {
        if (instance.MaterialOverride is not null)
            return "Scene Override";
        return ResolveMaterialName?.Invoke(instance)
            ?? (instance.Materials.Count > 0 ? instance.Materials[0].ToString() : "BuiltIn/Default");
    }

    /// <summary>
    /// Refreshes non-focused fields from the latest selected-node state.
    /// </summary>
    /// <returns>True when at least one displayed value changed.</returns>
    public bool RefreshValues()
    {
        var changed = false;
        foreach (var refresh in _refreshBindings)
            changed |= refresh();
        return changed;
    }

    /// <summary>Attaches a persistent game-script asset to the currently inspected node.</summary>
    /// <param name="scriptId">Persistent C# source asset identity.</param>
    /// <returns>True when an inspected node received the script type.</returns>
    public bool AttachScript(AssetId scriptId)
    {
        if (InspectedNode is not { } node)
            return false;
        node.ScriptId = scriptId;
        var field = Children.OfType<TextField>()
            .FirstOrDefault(element => element.Name == "ScriptAssetField");
        if (field is not null)
            field.Text = GetScriptDisplayName(scriptId);
        NodeChanged?.Invoke(node);
        return true;
    }

    /// <summary>Assigns a persistent material resource to slot zero.</summary>
    /// <param name="material">Material asset or imported sub-asset.</param>
    /// <returns>True when an inspected mesh received the assignment.</returns>
    public bool AssignMaterial(AssetReference material)
    {
        if (InspectedNode is not MeshInstance3D instance)
            return false;
        instance.Materials.Clear();
        instance.Materials.Add(material);
        instance.MaterialOverride = null;
        NodeChanged?.Invoke(instance);
        Bind(instance);
        return true;
    }

    /// <summary>Assigns a base-color texture through a scene-local material override.</summary>
    /// <param name="texture">Texture asset or imported sub-asset.</param>
    /// <returns>True when an inspected mesh received the texture override.</returns>
    public bool AssignBaseColorTexture(AssetReference texture)
    {
        if (InspectedNode is not MeshInstance3D instance)
            return false;
        GetOrCreateOverride(instance).BaseColorTexture = texture;
        NodeChanged?.Invoke(instance);
        Bind(instance);
        return true;
    }

    /// <summary>Formats one optional script asset for the read-only Inspector field.</summary>
    /// <param name="scriptId">Optional persistent script asset identity.</param>
    /// <returns>Resolved project path, UUID fallback, or empty text.</returns>
    private string GetScriptDisplayName(AssetId? scriptId)
    {
        if (scriptId is not { } id)
            return string.Empty;
        return ResolveScriptName?.Invoke(id) ?? id.ToString();
    }

    /// <summary>
    /// Adds a three-component vector editor row.
    /// </summary>
    /// <param name="label">Row label.</param>
    /// <param name="namePrefix">Prefix assigned to field names.</param>
    /// <param name="y">Local row position.</param>
    /// <param name="read">Callback returning the latest vector value.</param>
    /// <param name="apply">Callback receiving valid edited values.</param>
    /// <param name="radiansAsDegrees">Whether displayed values convert radians to degrees.</param>
    private void AddVectorRow(
        string label,
        string namePrefix,
        float y,
        Func<Vector3> read,
        Action<Vector3> apply,
        bool radiansAsDegrees)
    {
        const float labelWidth = 66f;
        const float spacing = 4f;
        var availableWidth = MathF.Max(0f, Width - 24f - labelWidth);
        var fieldWidth = MathF.Floor((availableWidth - spacing * 2f) / 3f);
        var initialValue = read();
        var displayValue = radiansAsDegrees
            ? initialValue * (180f / MathF.PI) : initialValue;
        AddChild(CreateLabel(12f, y, labelWidth, 30f, label, _theme.TextSecondary));

        var fields = new TextField[3];
        for (var index = 0; index < fields.Length; index++)
        {
            var componentIndex = index;
            var field = new TextField(fieldWidth, 30f, _theme)
            {
                Name = $"{namePrefix}{"XYZ"[index]}",
                Text = Format(GetComponent(displayValue, index)),
                Margin = new Thickness(12f + labelWidth + index * (fieldWidth + spacing),
                    y, 0f, 0f)
            };
            field.TextChanged += text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var component))
                    return;
                var edited = read();
                var internalComponent = radiansAsDegrees
                    ? component * MathF.PI / 180f : component;
                edited = WithComponent(edited, componentIndex, internalComponent);
                apply(edited);
                if (InspectedNode is { } inspectedNode)
                    NodeChanged?.Invoke(inspectedNode);
            };
            RegisterRefresh(field, () =>
            {
                var latest = GetComponent(read(), componentIndex);
                if (radiansAsDegrees)
                    latest *= 180f / MathF.PI;
                return Format(latest);
            });
            fields[index] = field;
            AddChild(field);
        }
    }

    /// <summary>Adds a non-destructive field refresh binding.</summary>
    /// <param name="field">Text field to update while it is not focused.</param>
    /// <param name="read">Callback returning current display text.</param>
    private void RegisterRefresh(TextField field, Func<string> read)
    {
        _refreshBindings.Add(() =>
        {
            if (field.IsFocused)
                return false;
            var latest = read();
            if (field.Text == latest)
                return false;
            field.Text = latest;
            return true;
        });
    }

    /// <summary>Reads one vector component by zero-based index.</summary>
    /// <param name="value">Source vector.</param>
    /// <param name="index">Component index.</param>
    /// <returns>The selected component.</returns>
    private static float GetComponent(Vector3 value, int index)
    {
        return index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>Reads one four-component vector component.</summary>
    /// <param name="value">Source vector.</param>
    /// <param name="index">Component index.</param>
    /// <returns>The selected component.</returns>
    private static float GetComponent(Vector4 value, int index)
    {
        return index switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            3 => value.W,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>Returns a vector with one component replaced.</summary>
    /// <param name="value">Source vector.</param>
    /// <param name="index">Component index.</param>
    /// <param name="component">Replacement component.</param>
    /// <returns>The edited vector.</returns>
    private static Vector3 WithComponent(Vector3 value, int index, float component)
    {
        return index switch
        {
            0 => value with { X = component },
            1 => value with { Y = component },
            2 => value with { Z = component },
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>Returns a four-component vector with one component replaced.</summary>
    /// <param name="value">Source vector.</param>
    /// <param name="index">Component index.</param>
    /// <param name="component">Replacement component.</param>
    /// <returns>Edited vector.</returns>
    private static Vector4 WithComponent(Vector4 value, int index, float component)
    {
        return index switch
        {
            0 => value with { X = component },
            1 => value with { Y = component },
            2 => value with { Z = component },
            3 => value with { W = component },
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>Formats a component compactly using culture-independent text.</summary>
    /// <param name="value">Component value.</param>
    /// <returns>Editable numeric text.</returns>
    private static string Format(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Creates one consistently styled Inspector label.</summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Label width.</param>
    /// <param name="height">Label height.</param>
    /// <param name="text">Displayed text.</param>
    /// <param name="color">Text color.</param>
    /// <returns>The configured label.</returns>
    private Label CreateLabel(
        float x,
        float y,
        float width,
        float height,
        string text,
        Color color)
    {
        return new Label(text, width, height)
        {
            FontSize = _theme.FontSize,
            ForegroundColor = color,
            PaddingLeft = 0f,
            Margin = new Thickness(x, y, 0f, 0f)
        };
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        foreach (var child in Children.OfType<UIElement>())
        {
            child.Measure(contentSize);
            child.Arrange(Vector2.Zero, child.DesiredSize);
        }
    }

    /// <summary>Stores one retained node-specific Inspector view.</summary>
    /// <param name="Children">Constructed controls.</param>
    /// <param name="RefreshBindings">Value refresh callbacks associated with the controls.</param>
    private readonly record struct CachedInspectorView(
        UIElement[] Children,
        Func<bool>[] RefreshBindings);
}
