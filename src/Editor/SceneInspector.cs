using System.Globalization;
using System.Numerics;
using System.Reflection;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;
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
    private readonly List<ScriptFieldBinding> _scriptBindings = new();
    private readonly UIEditForm _editForm;
    private bool _scriptBindingsDirty;
    private bool _modelBindingsDirty;
    private Node? _subscribedNode;

    /// <summary>Gets or sets the editor display-name resolver for attached script assets.</summary>
    public Func<AssetId, string?>? ResolveScriptName { get; set; }

    /// <summary>Gets or sets the resolver for compiled script types in edit mode.</summary>
    public Func<AssetId, Type?>? ResolveScriptType { get; set; }

    /// <summary>Gets or sets the resolver for live script instances in play mode.</summary>
    public Func<ScriptComponent, SceneScript?>? ResolveScriptInstance { get; set; }

    /// <summary>Gets or sets the display-name resolver for a mesh material assignment.</summary>
    public Func<MeshInstance3D, string>? ResolveMaterialName { get; set; }

    /// <summary>Gets or sets the display-name resolver for generic persistent assets.</summary>
    public Func<AssetReference, string>? ResolveAssetReferenceName { get; set; }

    /// <summary>Gets or sets the reusable content factory for referenced assets.</summary>
    public Func<AssetReference, UIElement?>? CreateAssetInspectorContent { get; set; }

    /// <summary>Gets the node currently displayed by the Inspector.</summary>
    public Node? InspectedNode { get; private set; }

    /// <summary>Gets the exact component focused by Scene preview picking.</summary>
    public Component? FocusedComponent { get; private set; }

    /// <summary>Gets the active Inspector edit-form scope.</summary>
    public UIEditForm EditForm => _editForm;

    /// <summary>Gets or sets the host canvas used for floating component menus.</summary>
    public Canvas? PopupOverlay { get; set; }

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
        _editForm = new UIEditForm(this);
        PaintBackground = false;
        Bind((Node?)null);
    }

    /// <summary>
    /// Rebuilds Inspector fields for a selected scene node.
    /// </summary>
    /// <param name="node">Selected authored node, or null.</param>
    public void Bind(Node? node)
    {
        Bind(node, null);
    }

    /// <summary>Rebuilds Inspector fields and emphasizes one preview-picked component.</summary>
    /// <param name="node">Selected authored node, or null.</param>
    /// <param name="focusedComponent">Exact selected component, or null for node selection.</param>
    public void Bind(Node? node, Component? focusedComponent)
    {
        var focusChanged = !ReferenceEquals(FocusedComponent, focusedComponent);
        if (ReferenceEquals(InspectedNode, node) && !focusChanged && Children.Count > 0)
        {
            RefreshValues();
            return;
        }

        DeactivateInspectorContent();
        UnsubscribeFromNode();
        InspectedNode = node;
        FocusedComponent = focusedComponent;
        DeactivateScriptBindings();
        _editForm.Clear();
        ClearChildren();
        _refreshBindings.Clear();
        _scriptBindings.Clear();
        if (node is null)
        {
            AddChild(CreateLabel(12f, 12f, Width - 24f, 28f,
                "Select an object to inspect", _theme.TextMuted));
            return;
        }
        if (!focusChanged && RestoreCachedView(node))
        {
            SubscribeToNode(node);
            return;
        }

        AddChild(CreateLabel(12f, 8f, MathF.Max(0f, Width - 148f), 24f,
            node.GetType().Name, _theme.TextSecondary));
        AddEditActions();
        AddChild(CreateLabel(12f, 40f, 58f, 30f, "Name", _theme.TextSecondary));
        var nameField = new TextField(Width - 84f, 30f, _theme)
        {
            Name = "NameField",
            Text = node.Name,
            UpdateTrigger = TextUpdateTrigger.TextChanged,
            Margin = new Thickness(72f, 40f, 0f, 0f)
        };
        nameField.ValueUpdateRequested += value =>
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
        if (node is Light3D light)
            scriptY = AddLightSection(light, scriptY);
        if (node is Skybox3D skybox)
            scriptY = AddSkyboxSection(skybox, scriptY);
        if (node is MeshInstance3D meshInstance)
        {
            var terrain = meshInstance.GetComponent<TerrainColliderComponent>();
            if (terrain?.TerrainData is { } terrainReference)
            {
                scriptY = AddTerrainAssetSection(terrainReference, 236f);
                scriptY = AddMaterialSection(meshInstance, scriptY);
            }
            else
                scriptY = AddMaterialSection(meshInstance, 236f);
        }

        scriptY = AddPhysicsSections(node, scriptY);
        AddScriptSections(node, scriptY);
        CacheCurrentView(node);
        ActivateInspectorContent();
        SubscribeToNode(node);
    }

    /// <summary>Displays provider-created properties for a non-scene selection.</summary>
    /// <param name="document">Provider-independent Inspector content.</param>
    public void Bind(InspectorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        DeactivateInspectorContent();
        UnsubscribeFromNode();
        InspectedNode = null;
        FocusedComponent = null;
        DeactivateScriptBindings();
        _editForm.Clear();
        ClearChildren();
        _refreshBindings.Clear();
        _scriptBindings.Clear();
        AddChild(CreateLabel(12f, 8f, Width - 24f, 24f,
            document.Title, _theme.TextSecondary));
        AddChild(CreateLabel(12f, 40f, Width - 24f, 30f,
            document.DisplayName, _theme.TextPrimary));
        var content = document.Content;
        content.Margin = new Thickness(12f, 82f, 12f, 0f);
        content.Width = 0f;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        AddChild(content);
        ActivateInspectorContent();
    }

    /// <summary>Adds shared and type-specific settings for one authored light.</summary>
    /// <param name="light">Inspected light.</param>
    /// <param name="y">Available section top.</param>
    /// <returns>Top available for following sections.</returns>
    private float AddLightSection(Light3D light, float y)
    {
        AddChild(CreateLabel(12f, y, Width - 24f, 26f,
            light switch
            {
                DirectionalLight3D => "Directional Light",
                PointLight3D => "Point Light",
                SpotLight3D => "Spot Light",
                _ => "Light"
            }, _theme.TextPrimary));
        AddColorRow("Color", "LightColor", y + 30f, () => light.Color,
            value => light.Color = value);
        AddNonnegativeFloatField("Intensity", "LightIntensity", y + 68f,
            () => light.Intensity, value => light.Intensity = value);
        var rowY = y + 106f;
        if (light is DirectionalLight3D directional)
        {
            AddNonnegativeFloatField("Ambient", "LightAmbientIntensity", rowY,
                () => directional.AmbientIntensity,
                value => directional.AmbientIntensity = value);
            rowY += 38f;
        }
        if (light is PointLight3D point)
        {
            AddNonnegativeFloatField("Range", "LightRange", rowY,
                () => point.Range, value => point.Range = value);
            rowY += 38f;
        }
        if (light is SpotLight3D spot)
        {
            AddNonnegativeFloatField("Range", "LightRange", rowY,
                () => spot.Range, value => spot.Range = value);
            AddNonnegativeFloatField("Inner", "LightInnerAngle", rowY + 38f,
                () => spot.InnerAngle, value => spot.InnerAngle = value);
            AddNonnegativeFloatField("Outer", "LightOuterAngle", rowY + 76f,
                () => spot.OuterAngle, value => spot.OuterAngle = value);
            rowY += 114f;
        }
        var toggleWidth = (Width - 30f) * 0.5f;
        var enabled = new ToggleButton(toggleWidth, 30f, "Enabled", _theme)
        {
            Name = "LightEnabled",
            IsChecked = light.IsEnabled,
            Margin = new Thickness(12f, rowY, 0f, 0f)
        };
        enabled.CheckedChanged += value =>
        {
            light.IsEnabled = value;
            if (InspectedNode is { } node)
                NodeChanged?.Invoke(node);
        };
        AddChild(enabled);
        var shadows = new ToggleButton(toggleWidth, 30f, "Shadows", _theme)
        {
            Name = "LightCastsShadows",
            IsChecked = light.CastsShadows,
            Margin = new Thickness(18f + toggleWidth, rowY, 0f, 0f)
        };
        shadows.CheckedChanged += value =>
        {
            light.CastsShadows = value;
            if (InspectedNode is { } node)
                NodeChanged?.Invoke(node);
        };
        AddChild(shadows);
        return rowY + 42f;
    }

    /// <summary>Adds equirectangular texture and display settings for one skybox.</summary>
    /// <param name="skybox">Inspected skybox.</param>
    /// <param name="y">Available section top.</param>
    /// <returns>Top available for following sections.</returns>
    private float AddSkyboxSection(Skybox3D skybox, float y)
    {
        AddChild(CreateLabel(12f, y, Width - 24f, 26f, "Skybox", _theme.TextPrimary));
        AddAssetReferenceRow("Texture", "SkyboxTexture", y + 30f, "nico/texture2d",
            () => skybox.Texture, value => skybox.Texture = value);
        AddColorRow("Tint", "SkyboxTint", y + 68f, () => skybox.Tint,
            value => skybox.Tint = value);
        AddNonnegativeFloatField("Intensity", "SkyboxIntensity", y + 106f,
            () => skybox.Intensity, value => skybox.Intensity = value);
        var enabled = new ToggleButton(Width - 24f, 30f, "Enabled", _theme)
        {
            Name = "SkyboxEnabled",
            IsChecked = skybox.IsEnabled,
            Margin = new Thickness(12f, y + 144f, 0f, 0f)
        };
        enabled.CheckedChanged += value =>
        {
            skybox.IsEnabled = value;
            NodeChanged?.Invoke(skybox);
        };
        AddChild(enabled);
        return y + 186f;
    }

    /// <summary>Adds one linear RGB color editor.</summary>
    /// <param name="label">Displayed field label.</param>
    /// <param name="name">Stable UI element name.</param>
    /// <param name="y">Field row position.</param>
    /// <param name="read">Current color reader.</param>
    /// <param name="apply">Color writer.</param>
    private void AddColorRow(string label, string name, float y,
        Func<Vector3> read, Action<Vector3> apply)
    {
        AddChild(CreateLabel(12f, y, 66f, 30f, label, _theme.TextSecondary));
        var color = read();
        var picker = new ColorPicker(Width - 90f, 30f, showAlpha: false, _theme)
        {
            Name = name,
            Value = new Vector4(color, 1f),
            Margin = new Thickness(78f, y, 0f, 0f)
        };
        picker.ValueChanged += value =>
        {
            apply(new Vector3(value.X, value.Y, value.Z));
            if (InspectedNode is { } node)
                NodeChanged?.Invoke(node);
        };
        RegisterRefresh(picker, () =>
        {
            var latest = read();
            return new Vector4(latest, 1f);
        });
        AddChild(picker);
    }

    /// <summary>Adds one nonnegative scalar field.</summary>
    /// <param name="label">Displayed field label.</param>
    /// <param name="name">Stable UI element name.</param>
    /// <param name="y">Field row position.</param>
    /// <param name="read">Current value reader.</param>
    /// <param name="apply">Validated value writer.</param>
    private void AddNonnegativeFloatField(string label, string name, float y,
        Func<float> read, Action<float> apply)
    {
        AddChild(CreateLabel(12f, y, 66f, 30f, label, _theme.TextSecondary));
        var field = new TextField(Width - 90f, 30f, _theme)
        {
            Name = name,
            Text = Format(read()),
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = text => ValidateConstrainedFloat(text, false, true),
            Margin = new Thickness(78f, y, 0f, 0f)
        };
        field.ValueUpdateRequested += text =>
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var value) || value < 0f)
                return;
            apply(value);
            if (InspectedNode is { } node)
                NodeChanged?.Invoke(node);
        };
        RegisterRefresh(field, () => Format(read()));
        _editForm.Register(field);
        AddChild(field);
    }

    /// <summary>Adds concrete rigid-body and collider fields in authored component order.</summary>
    /// <param name="node">Inspected component owner.</param>
    /// <param name="y">Available section top.</param>
    /// <returns>Top available for the next section.</returns>
    private float AddPhysicsSections(Node node, float y)
    {
        var components = node.Components;
        var physicsIndex = 0;
        if (FocusedComponent is RigidBodyComponent focusedBody &&
            ReferenceEquals(focusedBody.Owner, node))
            y = AddRigidBodySection(focusedBody, y, physicsIndex++);
        else if (FocusedComponent is ColliderComponent focusedCollider &&
            ReferenceEquals(focusedCollider.Owner, node))
            y = AddColliderSection(focusedCollider, y, physicsIndex++);
        for (var index = 0; index < components.Count; index++)
        {
            if (ReferenceEquals(components[index], FocusedComponent))
                continue;
            switch (components[index])
            {
                case RigidBodyComponent rigidBody:
                    y = AddRigidBodySection(rigidBody, y, physicsIndex++);
                    break;
                case ColliderComponent collider:
                    y = AddColliderSection(collider, y, physicsIndex++);
                    break;
            }
        }
        return y;
    }

    /// <summary>Adds the core editable rigid-body fields.</summary>
    /// <param name="body">Component being edited.</param><param name="y">Section top.</param>
    /// <param name="index">Unique component display index.</param><returns>Following section top.</returns>
    private float AddRigidBodySection(RigidBodyComponent body, float y, int index)
    {
        AddComponentHeader(body.Owner!, body, "Rigid Body", $"RigidBody{index}", y);
        AddFloatRow("Mass", $"RigidBody{index}Mass", y + 30f, () => body.Mass,
            value => body.Mass = value, positive: true);
        AddFloatRow("Gravity", $"RigidBody{index}Gravity", y + 68f,
            () => body.GravityScale, value => body.GravityScale = value);
        AddFloatRow("Damping", $"RigidBody{index}Damping", y + 106f,
            () => body.LinearDamping, value => body.LinearDamping = value, nonnegative: true);
        return y + 148f;
    }

    /// <summary>Adds shared and type-specific fields for one concrete collider.</summary>
    /// <param name="collider">Collider being edited.</param><param name="y">Section top.</param>
    /// <param name="index">Unique component display index.</param><returns>Following section top.</returns>
    private float AddColliderSection(ColliderComponent collider, float y, int index)
    {
        var prefix = $"Collider{index}";
        AddComponentHeader(collider.Owner!, collider,
            GetColliderDisplayName(collider) +
            (ReferenceEquals(collider, FocusedComponent) ? " (Selected)" : string.Empty),
            prefix, y);
        AddVectorRow("Center", prefix + "Center", y + 30f, () => collider.Center,
            value => collider.Center = value, radiansAsDegrees: false);
        var row = y + 68f;
        switch (collider)
        {
            case BoxColliderComponent box:
                AddVectorRow("Size", prefix + "Size", row, () => box.Size,
                    value => box.Size = value, radiansAsDegrees: false, positive: true);
                row += 38f;
                break;
            case SphereColliderComponent sphere:
                AddFloatRow("Radius", prefix + "Radius", row, () => sphere.Radius,
                    value => sphere.Radius = value, positive: true);
                row += 38f;
                break;
            case CapsuleColliderComponent capsule:
                AddFloatRow("Radius", prefix + "Radius", row, () => capsule.Radius,
                    value => capsule.Radius = value, positive: true);
                AddFloatRow("Height", prefix + "Height", row + 38f, () => capsule.Height,
                    value => capsule.Height = value, positive: true);
                row += 76f;
                break;
            case CylinderColliderComponent cylinder:
                AddFloatRow("Radius", prefix + "Radius", row, () => cylinder.Radius,
                    value => cylinder.Radius = value, positive: true);
                AddFloatRow("Height", prefix + "Height", row + 38f, () => cylinder.Height,
                    value => cylinder.Height = value, positive: true);
                row += 76f;
                break;
            case PlaneColliderComponent plane:
                AddVector2Row("Size", prefix + "Size", row, () => plane.Size,
                    value => plane.Size = value);
                row += 38f;
                break;
            case MeshColliderComponent mesh:
                AddAssetReferenceRow("Mesh", prefix + "Mesh", row, "nico/static-mesh", () => mesh.Mesh,
                    value => mesh.Mesh = value);
                row += 38f;
                if (mesh.Mesh is null)
                {
                    AddChild(CreateLabel(78f, row, Width - 90f, 26f,
                        "Missing explicit collision mesh", _theme.Error));
                    row += 30f;
                }
                break;
            case TerrainColliderComponent terrain:
                AddAssetReferenceRow("Terrain", prefix + "Terrain", row,
                    "nico/terrain",
                    () => terrain.TerrainData, value => AssignTerrainData(terrain, value));
                AddVector2Row("Size", prefix + "Size", row + 38f,
                    () => terrain.HorizontalSize, value => terrain.HorizontalSize = value);
                AddFloatRow("Height", prefix + "Height", row + 76f,
                    () => terrain.HeightScale, value => terrain.HeightScale = value,
                    positive: true);
                row += 114f;
                if (terrain.TerrainData is null)
                {
                    AddChild(CreateLabel(78f, row, Width - 90f, 26f,
                        "Missing explicit terrain data", _theme.Error));
                    row += 30f;
                }
                break;
        }
        AddFloatRow("Friction", prefix + "Friction", row, () => collider.Friction,
            value => collider.Friction = Math.Clamp(value, 0f, 1f), nonnegative: true);
        AddFloatRow("Bounce", prefix + "Restitution", row + 38f,
            () => collider.Restitution,
            value => collider.Restitution = Math.Clamp(value, 0f, 1f), nonnegative: true);
        AddUIntRow("Layer", prefix + "Layer", row + 76f, () => collider.CollisionLayer,
            value => collider.CollisionLayer = value, singleBit: true);
        AddUIntRow("Mask", prefix + "Mask", row + 114f, () => collider.CollisionMask,
            value => collider.CollisionMask = value);
        var trigger = new ToggleButton(Width - 24f, 30f, "Trigger", _theme)
        {
            Name = prefix + "Trigger",
            IsChecked = collider.IsTrigger,
            Margin = new Thickness(12f, row + 152f, 0f, 0f)
        };
        trigger.CheckedChanged += value =>
        {
            collider.IsTrigger = value;
            if (InspectedNode is { } inspected)
                NodeChanged?.Invoke(inspected);
        };
        AddChild(trigger);
        return row + 194f;
    }

    /// <summary>Adds a component heading with an owner-anchored settings menu.</summary>
    /// <param name="node">Component owner.</param>
    /// <param name="component">Exact component represented by the section.</param>
    /// <param name="title">Displayed section title.</param>
    /// <param name="namePrefix">Stable control-name prefix.</param>
    /// <param name="y">Section top.</param>
    private Label AddComponentHeader(Node node, Component component, string title,
        string namePrefix, float y)
    {
        var header = CreateLabel(12f, y, MathF.Max(0f, Width - 60f), 26f,
            title, _theme.TextPrimary);
        AddChild(header);
        var settings = new Button(26f, 26f, _theme.Surface)
        {
            Name = namePrefix + "Settings",
            Content = new Icon(IconKind.Settings, 16f),
            Margin = new Thickness(MathF.Max(12f, Width - 38f), y, 0f, 0f)
        };
        var menu = new ContextMenu(140f, _theme)
        {
            Name = namePrefix + "SettingsMenu",
            Owner = settings,
            Placement = PopupPlacement.Below,
            ConstraintMargin = 8f,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        menu.AddItem("Remove", () => RemoveComponent(node, component));
        menu.Close();
        settings.Click += () =>
        {
            if (menu.IsOpen)
                menu.Close();
            else
            {
                menu.Open();
                PopupOverlay?.PlacePopup(menu);
            }
        };
        AddChild(settings);
        if (PopupOverlay is { } overlay)
            overlay.Add(menu, Vector2.Zero);
        else
            AddChild(menu);
        return header;
    }

    /// <summary>Gets a human-readable concrete collider heading.</summary>
    /// <param name="collider">Collider to name.</param><returns>Inspector heading.</returns>
    private static string GetColliderDisplayName(ColliderComponent collider) => collider switch
    {
        BoxColliderComponent => "Box Collider",
        SphereColliderComponent => "Sphere Collider",
        CapsuleColliderComponent => "Capsule Collider",
        CylinderColliderComponent => "Cylinder Collider",
        PlaneColliderComponent => "Plane Collider",
        MeshColliderComponent => "Mesh Collider",
        TerrainColliderComponent => "Terrain Collider",
        _ => "Collider"
    };


    /// <summary>Restores a previously constructed Inspector view for a node.</summary>
    /// <param name="node">Node whose view should be restored.</param>
    /// <returns>True when a retained view was restored.</returns>
    private bool RestoreCachedView(Node node)
    {
        if (!_cachedViews.TryGetValue(node, out var cached))
            return false;
        foreach (var child in cached.Children)
        {
            AddChild(child);
            if (child is TextBox editor && !editor.IsReadOnly &&
                editor.UpdateTrigger != TextUpdateTrigger.TextChanged)
                _editForm.Register(editor);
        }
        _refreshBindings.AddRange(cached.RefreshBindings);
        _scriptBindings.AddRange(cached.ScriptBindings);
        for (var index = 0; index < _scriptBindings.Count; index++)
            _scriptBindings[index].Activate();
        RefreshValues();
        ActivateInspectorContent();
        return true;
    }

    /// <summary>Retains the current Inspector controls and refresh bindings for reuse.</summary>
    /// <param name="node">Node owning the constructed view.</param>
    private void CacheCurrentView(Node node)
    {
        _cachedViews[node] = new CachedInspectorView(
            Children.OfType<UIElement>().ToArray(),
            _refreshBindings.ToArray(),
            _scriptBindings.ToArray());
    }

    /// <summary>Activates lifecycle-aware content in the current visual subtree.</summary>
    private void ActivateInspectorContent()
    {
        VisitInspectorContent(this, activate: true);
    }

    /// <summary>Deactivates lifecycle-aware content leaving the current visual subtree.</summary>
    private void DeactivateInspectorContent()
    {
        VisitInspectorContent(this, activate: false);
    }

    /// <summary>Visits lifecycle-aware Inspector descendants.</summary>
    /// <param name="root">Subtree root.</param>
    /// <param name="activate">True to activate; false to deactivate.</param>
    private static void VisitInspectorContent(UIElement root, bool activate)
    {
        if (root is IInspectorContentLifecycle lifecycle)
        {
            if (activate)
                lifecycle.Activate();
            else
                lifecycle.Deactivate();
        }
        var children = root.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                VisitInspectorContent(child, activate);
        }
    }

    /// <summary>Rebuilds script sections after compiled schemas or runtime instances change.</summary>
    public void RefreshScriptSchemas()
    {
        if (InspectedNode is not { } node)
            return;
        _cachedViews.Remove(node);
        InspectedNode = null;
        Bind(node);
    }

    /// <summary>Adds all script components and generated Editor-observed properties.</summary>
    /// <param name="node">Inspected component owner.</param>
    /// <param name="y">Top of the script section.</param>
    private void AddScriptSections(Node node, float y)
    {
        AddChild(CreateLabel(12f, y, Width - 24f, 26f, "Scripts", _theme.TextPrimary));
        var components = node.Components;
        var scriptIndex = 0;
        var rowY = y + 30f;
        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            if (components[componentIndex] is not ScriptComponent component)
                continue;
            var currentIndex = scriptIndex++;
            var title = Path.GetFileName(GetScriptDisplayName(component.ScriptId));
            var header = AddComponentHeader(node, component,
                string.IsNullOrWhiteSpace(title) ? "Script" : title,
                $"Script{currentIndex}", rowY);
            header.Name = $"ScriptHeader{currentIndex}";
            ConfigureScriptReorder(node, component, header);
            rowY += 30f;
            if (!TryResolveScript(component, out var script))
                continue;
            var descriptors = script.ObservedProperties;
            for (var descriptorIndex = 0; descriptorIndex < descriptors.Count; descriptorIndex++)
            {
                var descriptor = descriptors[descriptorIndex];
                if ((descriptor.Scope & ObserveScope.Editor) == 0 ||
                    !script.TryGetObservedValue(descriptor.Id, out var value))
                    continue;
                AddChild(CreateLabel(20f, rowY, 92f, 30f,
                    descriptor.Name, _theme.TextSecondary));
                var field = new TextField(Width - 136f, 30f, _theme)
                {
                    Name = $"ScriptProperty{currentIndex}_{descriptor.Name}",
                    Text = FormatObservedValue(value),
                    UpdateTrigger = TextUpdateTrigger.Commit,
                    Validator = text => ValidateObservedText(descriptor.Kind, text),
                    Margin = new Thickness(112f, rowY, 0f, 0f)
                };
                var binding = new ScriptFieldBinding(
                    script, component, descriptor, field,
                    () => NodeChanged?.Invoke(node),
                    () => _scriptBindingsDirty = true);
                field.ValueUpdateRequested += binding.ApplyText;
                _editForm.Register(field);
                _scriptBindings.Add(binding);
                binding.Activate();
                AddChild(field);
                rowY += 38f;
            }
        }
        AddChild(new TextField(Width - 24f, 30f, _theme)
        {
            Name = "ScriptAssetField",
            Placeholder = scriptIndex == 0
                ? "Drop script here" : "Drop another script here",
            IsReadOnly = true,
            AllowDrop = true,
            Margin = new Thickness(12f, rowY, 0f, 0f)
        });
    }

    /// <summary>Configures one script title as a move handle and insertion target.</summary>
    /// <param name="node">Script component owner.</param>
    /// <param name="component">Script represented by the title.</param>
    /// <param name="header">Title receiving drag behavior.</param>
    private void ConfigureScriptReorder(Node node, ScriptComponent component, Label header)
    {
        header.DragData = new UIDragData(
            new ScriptComponentDragData(node, component), header.Text);
        header.AllowedDragEffects = UIDragEffect.Move;
        header.AllowDrop = true;
        header.Drag += (_, dragEvent) => HandleScriptReorderDrag(
            node, component, header, dragEvent);
    }

    /// <summary>Negotiates and commits script-component insertion around one title.</summary>
    /// <param name="node">Drop-target owner.</param>
    /// <param name="target">Script represented by the drop-target title.</param>
    /// <param name="header">Drop-target title.</param>
    /// <param name="dragEvent">Current routed drag event.</param>
    private void HandleScriptReorderDrag(Node node, ScriptComponent target, Label header,
        UIDragEventArgs dragEvent)
    {
        if (!dragEvent.Data.TryGet<ScriptComponentDragData>(out var payload) || payload is null ||
            !ReferenceEquals(payload.Owner, node) || ReferenceEquals(payload.Component, target))
            return;
        var insertAfter = dragEvent.LocalPosition.Y >= header.Height * 0.5f;
        var targetIndex = IndexOfComponent(node, target);
        if (targetIndex < 0)
            return;
        dragEvent.DropIndicatorBounds = new UIClipRect(
            header.Left, insertAfter ? header.Bottom - 1f : header.Top,
            header.Right, insertAfter ? header.Bottom + 1f : header.Top + 2f);
        if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
            dragEvent.Effect = UIDragEffect.Move;
        else if (dragEvent.Kind == UIDragEventKind.Drop &&
                 node.MoveComponent(payload.Component, targetIndex + (insertAfter ? 1 : 0)))
        {
            NodeChanged?.Invoke(node);
            dragEvent.Effect = UIDragEffect.Move;
        }
        dragEvent.Handled = true;
    }

    /// <summary>Finds one component's authored-order index without interface enumeration.</summary>
    /// <param name="node">Component owner.</param>
    /// <param name="component">Component to locate.</param>
    /// <returns>Zero-based index, or minus one when absent.</returns>
    private static int IndexOfComponent(Node node, Component component)
    {
        var components = node.Components;
        for (var index = 0; index < components.Count; index++)
        {
            if (ReferenceEquals(components[index], component))
                return index;
        }
        return -1;
    }

    /// <summary>Removes one exact authored component and refreshes its owner.</summary>
    /// <param name="node">Component owner.</param>
    /// <param name="component">Exact component represented by the section.</param>
    private void RemoveComponent(Node node, Component component)
    {
        if (!node.RemoveComponent(component))
            return;
        NodeChanged?.Invoke(node);
    }

    /// <summary>Resolves a live script or creates an edit-mode schema instance.</summary>
    /// <param name="component">Script component to inspect.</param>
    /// <param name="script">Resolved script instance.</param>
    /// <returns>True when generated metadata is available.</returns>
    private bool TryResolveScript(ScriptComponent component, out SceneScript script)
    {
        if (ResolveScriptInstance?.Invoke(component) is { } live)
        {
            script = live;
            return true;
        }
        var type = ResolveScriptType?.Invoke(component.ScriptId);
        if (type is null || !typeof(SceneScript).IsAssignableFrom(type) || type.IsAbstract)
        {
            script = null!;
            return false;
        }
        SceneScript created;
        try
        {
            if (Activator.CreateInstance(type) is not SceneScript instance)
            {
                script = null!;
                return false;
            }
            created = instance;
        }
        catch (Exception exception) when (exception is TargetInvocationException or
                                           MemberAccessException or MissingMethodException)
        {
            script = null!;
            return false;
        }
        var overrides = component.PropertyOverrides;
        for (var index = 0; index < overrides.Count; index++)
        {
            var propertyOverride = overrides[index];
            if (ObservedValue.TryFromSerialized(propertyOverride.Value, out var value))
                created.TrySetObservedValue(propertyOverride.PropertyId, value);
        }
        script = created;
        return true;
    }

    /// <summary>Stops listening to script instances belonging to the outgoing cached view.</summary>
    private void DeactivateScriptBindings()
    {
        for (var index = 0; index < _scriptBindings.Count; index++)
            _scriptBindings[index].Deactivate();
    }

    /// <summary>Subscribes to coarse node changes for event-driven field synchronization.</summary>
    /// <param name="node">Currently inspected node.</param>
    private void SubscribeToNode(Node node)
    {
        _subscribedNode = node;
        node.Changed += OnInspectedNodeChanged;
    }

    /// <summary>Detaches the outgoing inspected-node subscription.</summary>
    private void UnsubscribeFromNode()
    {
        if (_subscribedNode is null)
            return;
        _subscribedNode.Changed -= OnInspectedNodeChanged;
        _subscribedNode = null;
    }

    /// <summary>Refreshes or rebuilds only after the selected model reports a change.</summary>
    /// <param name="kind">Coarse node change category.</param>
    private void OnInspectedNodeChanged(NodeChangeKind kind)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.Post(() => OnInspectedNodeChanged(kind));
            }
            catch (ObjectDisposedException)
            {
                UnsubscribeFromNode();
            }
            return;
        }
        if ((kind & NodeChangeKind.Components) != 0)
        {
            RefreshScriptSchemas();
            return;
        }
        if ((kind & NodeChangeKind.ComponentValues) != 0)
        {
            for (var index = 0; index < _scriptBindings.Count; index++)
                _scriptBindings[index].ApplyComponentValue();
        }
        _modelBindingsDirty = true;
        if (IsEffectivelyVisible)
            RefreshValues();
    }

    /// <summary>Adds slot-zero material assignment and embeds its shared asset editor.</summary>
    /// <param name="instance">Inspected mesh instance.</param>
    /// <param name="y">Section top.</param>
    /// <returns>Top available for following component sections.</returns>
    private float AddMaterialSection(MeshInstance3D instance, float y)
    {
        var isTerrain = instance.GetComponent<TerrainColliderComponent>() is not null;
        AddChild(CreateLabel(12f, y, Width - 24f, 26f,
            isTerrain ? "Terrain Material" : "Material", _theme.TextPrimary));
        var materialField = new AssetReferenceField(
            Width - 88f, 30f,
            isTerrain ? "nico/terrain-material" : "nico/standard-material",
            AssignMaterial, _theme)
        {
            Name = "MaterialSlot0",
            Text = GetMaterialName(instance),
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
            instance.Materials.Clear();
            NodeChanged?.Invoke(instance);
            Bind(instance);
        };
        AddChild(reset);

        var reference = instance.Materials.FirstOrDefault();
        if (reference.Asset.Value != Guid.Empty &&
            CreateAssetInspectorContent?.Invoke(reference) is { } content)
        {
            content.Margin = new Thickness(12f, y + 68f, 12f, 0f);
            content.Width = 0f;
            content.HorizontalAlignment = HorizontalAlignment.Stretch;
            AddChild(content);
        }
        return y + 344f;
    }

    /// <summary>Embeds the shared terrain asset editor above terrain collision settings.</summary>
    /// <param name="reference">Referenced editable terrain source.</param>
    /// <param name="y">Section top.</param>
    /// <returns>Top available for following component sections.</returns>
    private float AddTerrainAssetSection(AssetReference reference, float y)
    {
        AddChild(CreateLabel(12f, y, Width - 24f, 26f,
            "Terrain", _theme.TextPrimary));
        if (CreateAssetInspectorContent?.Invoke(reference) is not { } content)
        {
            AddChild(CreateLabel(12f, y + 30f, Width - 24f, 26f,
                "Terrain source is unavailable", _theme.Error));
            return y + 64f;
        }
        content.Margin = new Thickness(12f, y + 30f, 12f, 0f);
        content.Width = 0f;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        AddChild(content);
        return y + 30f + content.Height + 8f;
    }

    /// <summary>Formats the current slot-zero material ownership.</summary>
    /// <param name="instance">Mesh instance.</param>
    /// <returns>Material display name.</returns>
    private string GetMaterialName(MeshInstance3D instance)
    {
        return ResolveMaterialName?.Invoke(instance)
            ?? (instance.Materials.Count > 0 ? instance.Materials[0].ToString() : "BuiltIn/Default");
    }

    /// <summary>
    /// Refreshes non-focused fields from the latest selected-node state.
    /// </summary>
    /// <returns>True when at least one displayed value changed.</returns>
    public bool RefreshValues()
    {
        if (!IsEffectivelyVisible)
            return false;
        _modelBindingsDirty = false;
        var changed = false;
        foreach (var refresh in _refreshBindings)
            changed |= refresh();
        if (_scriptBindingsDirty)
        {
            _scriptBindingsDirty = false;
            for (var index = 0; index < _scriptBindings.Count; index++)
                changed |= _scriptBindings[index].SynchronizeIfDirty();
        }
        return changed;
    }

    /// <summary>Attaches a persistent game-script asset to the currently inspected node.</summary>
    /// <param name="scriptId">Persistent C# source asset identity.</param>
    /// <returns>True when an inspected node received the script type.</returns>
    public bool AttachScript(AssetId scriptId)
    {
        if (InspectedNode is not { } node)
            return false;
        node.AddComponent(new ScriptComponent(scriptId));
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
        NodeChanged?.Invoke(instance);
        Bind(instance);
        return true;
    }

    /// <summary>Assigns one terrain source to collision and matching visual geometry.</summary>
    /// <param name="terrain">Terrain collider receiving the source.</param>
    /// <param name="reference">Persistent terrain reference, or null.</param>
    private static void AssignTerrainData(
        TerrainColliderComponent terrain,
        AssetReference? reference)
    {
        terrain.TerrainData = reference;
        if (reference is { } value && terrain.Owner is MeshInstance3D instance)
            instance.Mesh = value;
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

    /// <summary>Adds a validated scalar component field.</summary>
    /// <param name="label">Displayed label.</param><param name="name">Field name.</param>
    /// <param name="y">Row position.</param><param name="read">Current value reader.</param>
    /// <param name="apply">Validated value writer.</param><param name="positive">Require greater than zero.</param>
    /// <param name="nonnegative">Require zero or greater.</param>
    private void AddFloatRow(string label, string name, float y, Func<float> read,
        Action<float> apply, bool positive = false, bool nonnegative = false)
    {
        AddChild(CreateLabel(12f, y, 66f, 30f, label, _theme.TextSecondary));
        var field = new TextField(Width - 90f, 30f, _theme)
        {
            Name = name,
            Text = Format(read()),
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = text => ValidateConstrainedFloat(text, positive, nonnegative),
            Margin = new Thickness(78f, y, 0f, 0f)
        };
        field.ValueUpdateRequested += text =>
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var value) || positive && value <= 0f || nonnegative && value < 0f)
                return;
            apply(value);
            if (InspectedNode is { } inspected)
                NodeChanged?.Invoke(inspected);
        };
        RegisterRefresh(field, () => Format(read()));
        _editForm.Register(field);
        AddChild(field);
    }

    /// <summary>Adds a two-component positive dimension field row.</summary>
    /// <param name="label">Displayed label.</param><param name="namePrefix">Field prefix.</param>
    /// <param name="y">Row position.</param><param name="read">Current vector reader.</param>
    /// <param name="apply">Validated vector writer.</param>
    private void AddVector2Row(string label, string namePrefix, float y, Func<Vector2> read,
        Action<Vector2> apply)
    {
        AddChild(CreateLabel(12f, y, 66f, 30f, label, _theme.TextSecondary));
        var width = MathF.Floor((Width - 94f) * .5f);
        for (var index = 0; index < 2; index++)
        {
            var componentIndex = index;
            var field = new TextField(width, 30f, _theme)
            {
                Name = namePrefix + "XZ"[index],
                Text = Format(index == 0 ? read().X : read().Y),
                UpdateTrigger = TextUpdateTrigger.Commit,
                Validator = text => ValidateConstrainedFloat(text, true, false),
                Margin = new Thickness(78f + index * (width + 4f), y, 0f, 0f)
            };
            field.ValueUpdateRequested += text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var value) || value <= 0f)
                    return;
                var current = read();
                apply(componentIndex == 0 ? current with { X = value } : current with { Y = value });
                if (InspectedNode is { } inspected)
                    NodeChanged?.Invoke(inspected);
            };
            RegisterRefresh(field, () => Format(componentIndex == 0 ? read().X : read().Y));
            _editForm.Register(field);
            AddChild(field);
        }
    }

    /// <summary>Adds an editable unsigned collision layer or mask.</summary>
    /// <param name="label">Displayed label.</param><param name="name">Field name.</param>
    /// <param name="y">Row position.</param><param name="read">Current value reader.</param>
    /// <param name="apply">Value writer.</param>
    /// <param name="singleBit">Whether the value must contain exactly one set bit.</param>
    private void AddUIntRow(string label, string name, float y, Func<uint> read,
        Action<uint> apply, bool singleBit = false)
    {
        AddChild(CreateLabel(12f, y, 66f, 30f, label, _theme.TextSecondary));
        var field = new TextField(Width - 90f, 30f, _theme)
        {
            Name = name,
            Text = read().ToString(CultureInfo.InvariantCulture),
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = text => ValidateUnsigned(text, singleBit),
            Margin = new Thickness(78f, y, 0f, 0f)
        };
        field.ValueUpdateRequested += text =>
        {
            if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var value) || singleBit &&
                (value == 0u || (value & (value - 1u)) != 0u))
                return;
            apply(value);
            if (InspectedNode is { } inspected)
                NodeChanged?.Invoke(inspected);
        };
        RegisterRefresh(field, () => read().ToString(CultureInfo.InvariantCulture));
        _editForm.Register(field);
        AddChild(field);
    }

    /// <summary>Validates an unsigned integer and optional single-bit layer constraint.</summary>
    /// <param name="text">Pending text.</param><param name="singleBit">Require exactly one bit.</param>
    /// <returns>Error message or null.</returns>
    private static string? ValidateUnsigned(string text, bool singleBit)
    {
        if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var value))
            return "Enter an unsigned integer.";
        return singleBit && (value == 0u || (value & (value - 1u)) != 0u)
            ? "Choose exactly one layer bit." : null;
    }

    /// <summary>Adds a typed drag/drop asset-reference field.</summary>
    /// <param name="label">Displayed label.</param><param name="name">Field name.</param>
    /// <param name="y">Row position.</param><param name="contentType">Accepted artifact type.</param>
    /// <param name="read">Current reference reader.</param>
    /// <param name="apply">Reference writer.</param>
    private void AddAssetReferenceRow(string label, string name, float y, string contentType,
        Func<AssetReference?> read, Action<AssetReference?> apply)
    {
        AddChild(CreateLabel(12f, y, 66f, 30f, label, _theme.TextSecondary));
        var field = new AssetReferenceField(Width - 90f, 30f, contentType, reference =>
        {
            apply(reference);
            if (InspectedNode is not { } inspected)
                return false;
            NodeChanged?.Invoke(inspected);
            if (contentType == "nico/terrain")
            {
                var focused = FocusedComponent;
                _cachedViews.Remove(inspected);
                InspectedNode = null;
                Bind(inspected, focused);
            }
            return true;
        }, _theme)
        {
            Name = name,
            Text = GetAssetReferenceName(read()),
            Placeholder = $"Drop required {label.ToLowerInvariant()} asset",
            Margin = new Thickness(78f, y, 0f, 0f)
        };
        RegisterRefresh(field, () => GetAssetReferenceName(read()));
        AddChild(field);
    }

    /// <summary>Formats one optional generic asset reference for an Inspector field.</summary>
    /// <param name="reference">Optional reference.</param>
    /// <returns>Resolved display name, diagnostic identity, or empty text.</returns>
    private string GetAssetReferenceName(AssetReference? reference)
    {
        return reference is not { } value
            ? string.Empty
            : ResolveAssetReferenceName?.Invoke(value) ?? value.ToString();
    }

    /// <summary>Validates a finite scalar with optional sign constraints.</summary>
    /// <param name="text">Pending text.</param><param name="positive">Require greater than zero.</param>
    /// <param name="nonnegative">Require zero or greater.</param><returns>Error or null.</returns>
    private static string? ValidateConstrainedFloat(string text, bool positive, bool nonnegative)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value) || !float.IsFinite(value))
            return "Enter a finite number.";
        if (positive && value <= 0f)
            return "Enter a value greater than zero.";
        if (nonnegative && value < 0f)
            return "Enter zero or a positive value.";
        return null;
    }

    /// <summary>Adds a three-component vector editor row.</summary>
    /// <param name="label">Row label.</param><param name="namePrefix">Field-name prefix.</param>
    /// <param name="y">Local row position.</param><param name="read">Current vector reader.</param>
    /// <param name="apply">Validated vector writer.</param>
    /// <param name="radiansAsDegrees">Whether display values use degrees.</param>
    /// <param name="positive">Whether each component must be greater than zero.</param>
    /// <param name="nonnegative">Whether each component must be zero or greater.</param>
    private void AddVectorRow(
        string label,
        string namePrefix,
        float y,
        Func<Vector3> read,
        Action<Vector3> apply,
        bool radiansAsDegrees,
        bool positive = false,
        bool nonnegative = false)
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
                UpdateTrigger = TextUpdateTrigger.Commit,
                Validator = text => positive || nonnegative
                    ? ValidateConstrainedFloat(text, positive, nonnegative)
                    : ValidateFloat(text),
                Margin = new Thickness(12f + labelWidth + index * (fieldWidth + spacing),
                    y, 0f, 0f)
            };
            field.ValueUpdateRequested += text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var component) || positive && component <= 0f ||
                    nonnegative && component < 0f)
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
            _editForm.Register(field);
            fields[index] = field;
            AddChild(field);
        }
    }

    /// <summary>Validates invariant floating-point Inspector input.</summary>
    /// <param name="text">Pending component text.</param>
    /// <returns>An error message, or null when parseable.</returns>
    private static string? ValidateFloat(string text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? null
            : "Enter a valid number.";

    /// <summary>Adds form-bound Apply and Revert actions to the Inspector header.</summary>
    private void AddEditActions()
    {
        var revert = new Button(54f, 26f, "Revert", _theme)
        {
            Name = "InspectorRevert",
            Margin = new Thickness(MathF.Max(12f, Width - 122f), 4f, 0f, 0f)
        };
        var apply = new Button(54f, 26f, "Apply", _theme, ButtonStyle.Primary)
        {
            Name = "InspectorApply",
            Margin = new Thickness(MathF.Max(70f, Width - 64f), 4f, 0f, 0f)
        };
        _editForm.BindCancelButton(revert);
        _editForm.BindCommitButton(apply);
        AddChild(revert);
        AddChild(apply);
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

    /// <summary>Adds a non-destructive color-picker refresh binding.</summary>
    /// <param name="picker">Color picker to synchronize.</param>
    /// <param name="read">Callback returning the current linear color.</param>
    private void RegisterRefresh(ColorPicker picker, Func<Vector4> read)
    {
        _refreshBindings.Add(() =>
        {
            var latest = read();
            if (picker.Value == latest)
                return false;
            picker.SetValueWithoutNotification(latest);
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

    /// <summary>Formats a generated observed value for a text editor.</summary>
    /// <param name="value">Typed generated value.</param>
    /// <returns>Invariant editable text.</returns>
    private static string FormatObservedValue(ObservedValue value)
    {
        return value.Kind switch
        {
            ObservedValueKind.Boolean when value.TryGetBoolean(out var boolean) =>
                boolean ? "true" : "false",
            ObservedValueKind.SignedInteger when value.TryGetSignedInteger(out var signed) =>
                signed.ToString(CultureInfo.InvariantCulture),
            ObservedValueKind.UnsignedInteger when value.TryGetUnsignedInteger(out var unsigned) =>
                unsigned.ToString(CultureInfo.InvariantCulture),
            ObservedValueKind.Number when value.TryGetNumber(out var number) =>
                number.ToString("G", CultureInfo.InvariantCulture),
            ObservedValueKind.String when value.TryGetString(out var text) => text ?? string.Empty,
            ObservedValueKind.Vector2 when value.TryGetVector2(out var vector2) =>
                FormattableString.Invariant($"{vector2.X:G}, {vector2.Y:G}"),
            ObservedValueKind.Vector3 when value.TryGetVector3(out var vector3) =>
                FormattableString.Invariant($"{vector3.X:G}, {vector3.Y:G}, {vector3.Z:G}"),
            ObservedValueKind.Vector4 when value.TryGetVector4(out var vector4) =>
                FormattableString.Invariant(
                    $"{vector4.X:G}, {vector4.Y:G}, {vector4.Z:G}, {vector4.W:G}"),
            _ => string.Empty
        };
    }

    /// <summary>Validates text for one generated observed value kind.</summary>
    /// <param name="kind">Expected generated value kind.</param>
    /// <param name="text">Pending invariant text.</param>
    /// <returns>An error message, or null when parseable.</returns>
    private static string? ValidateObservedText(ObservedValueKind kind, string text) =>
        TryParseObservedText(kind, text, out _) ? null : "Enter a valid value.";

    /// <summary>Parses invariant Inspector text into the generated value contract.</summary>
    /// <param name="kind">Expected generated value kind.</param>
    /// <param name="text">Committed text.</param>
    /// <param name="value">Parsed typed value.</param>
    /// <returns>True when parsing succeeds.</returns>
    private static bool TryParseObservedText(
        ObservedValueKind kind,
        string text,
        out ObservedValue value)
    {
        switch (kind)
        {
            case ObservedValueKind.Boolean when bool.TryParse(text, out var boolean):
                value = ObservedValue.From(boolean);
                return true;
            case ObservedValueKind.SignedInteger when long.TryParse(
                text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed):
                value = ObservedValue.From(signed);
                return true;
            case ObservedValueKind.UnsignedInteger when ulong.TryParse(
                text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned):
                value = ObservedValue.From(unsigned);
                return true;
            case ObservedValueKind.Number when double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                value = ObservedValue.From(number);
                return true;
            case ObservedValueKind.String:
                value = ObservedValue.From(text);
                return true;
            case ObservedValueKind.Vector2:
                return TryParseVector(text, 2, out value);
            case ObservedValueKind.Vector3:
                return TryParseVector(text, 3, out value);
            case ObservedValueKind.Vector4:
                return TryParseVector(text, 4, out value);
            default:
                value = default;
                return false;
        }
    }

    /// <summary>Parses a comma-separated two-, three-, or four-component vector.</summary>
    /// <param name="text">Invariant vector text.</param>
    /// <param name="componentCount">Required number of components.</param>
    /// <param name="value">Parsed observed vector.</param>
    /// <returns>True when every required component is valid.</returns>
    private static bool TryParseVector(string text, int componentCount, out ObservedValue value)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        Span<float> components = stackalloc float[4];
        if (parts.Length != componentCount)
        {
            value = default;
            return false;
        }
        for (var index = 0; index < parts.Length; index++)
        {
            if (!float.TryParse(parts[index], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out components[index]))
            {
                value = default;
                return false;
            }
        }
        value = componentCount switch
        {
            2 => ObservedValue.From(new Vector2(components[0], components[1])),
            3 => ObservedValue.From(new Vector3(components[0], components[1], components[2])),
            4 => ObservedValue.From(new Vector4(
                components[0], components[1], components[2], components[3])),
            _ => default
        };
        return value.Kind != ObservedValueKind.None;
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
            Padding = Thickness.Zero,
            Margin = new Thickness(x, y, 0f, 0f)
        };
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        if (_modelBindingsDirty || _scriptBindingsDirty)
            RefreshValues();
        foreach (var child in Children.OfType<UIElement>())
        {
            child.Measure(contentSize);
            child.Arrange(Vector2.Zero, child.DesiredSize);
        }
    }

    /// <summary>Connects one generated script property to one retained Inspector field.</summary>
    private sealed class ScriptFieldBinding
    {
        private readonly SceneScript _script;
        private readonly ScriptComponent _component;
        private readonly ObservedPropertyDescriptor _descriptor;
        private readonly TextField _field;
        private readonly Action _edited;
        private readonly Action _markDirty;
        private bool _active;
        private bool _dirty;

        /// <summary>Creates one generated-property binding.</summary>
        /// <param name="script">Schema or live script instance.</param>
        /// <param name="component">Persistent attachment receiving authored overrides.</param>
        /// <param name="descriptor">Generated property metadata.</param>
        /// <param name="field">Retained text editor.</param>
        /// <param name="edited">Callback invoked after an authored edit.</param>
        /// <param name="markDirty">Callback recording deferred inactive synchronization.</param>
        internal ScriptFieldBinding(
            SceneScript script,
            ScriptComponent component,
            ObservedPropertyDescriptor descriptor,
            TextField field,
            Action edited,
            Action markDirty)
        {
            _script = script;
            _component = component;
            _descriptor = descriptor;
            _field = field;
            _edited = edited;
            _markDirty = markDirty;
            _field.Blur += SynchronizeAfterEdit;
        }

        /// <summary>Subscribes while this cached Inspector view is selected.</summary>
        internal void Activate()
        {
            if (_active)
                return;
            _active = true;
            _script.ObservedPropertyChanged += OnPropertyChanged;
            _dirty = true;
            _markDirty();
        }

        /// <summary>Unsubscribes while this cached Inspector view is not selected.</summary>
        internal void Deactivate()
        {
            if (!_active)
                return;
            _script.ObservedPropertyChanged -= OnPropertyChanged;
            _active = false;
        }

        /// <summary>Applies committed Inspector text through the generated setter.</summary>
        /// <param name="text">Committed invariant text.</param>
        internal void ApplyText(string text)
        {
            if (!TryParseObservedText(_descriptor.Kind, text, out var requested) ||
                !_script.TrySetObservedValue(_descriptor.Id, requested) ||
                !_script.TryGetObservedValue(_descriptor.Id, out var applied))
            {
                _dirty = true;
                _markDirty();
                return;
            }
            if (applied.TryToSerialized(out var persistent))
                _component.SetPropertyOverride(_descriptor.Id, persistent);
            _edited();
        }

        /// <summary>Applies an authored override changed outside this Inspector field.</summary>
        internal void ApplyComponentValue()
        {
            if (!_component.TryGetPropertyOverride(_descriptor.Id, out var persistent) ||
                !ObservedValue.TryFromSerialized(persistent, out var value))
                return;
            _script.TrySetObservedValue(_descriptor.Id, value);
        }

        /// <summary>Synchronizes a deferred model change after visibility or focus permits it.</summary>
        /// <returns>True when displayed text changed.</returns>
        internal bool SynchronizeIfDirty()
        {
            if (!_dirty || !_field.IsEffectivelyVisible || _field.IsFocused)
                return false;
            _dirty = false;
            if (!_script.TryGetObservedValue(_descriptor.Id, out var value))
                return false;
            var text = FormatObservedValue(value);
            if (_field.Text == text)
                return false;
            _field.Text = text;
            return true;
        }

        /// <summary>Defers one generated property change to the owning UI thread when required.</summary>
        /// <param name="change">Generated property transition.</param>
        private void OnPropertyChanged(ObservedPropertyChange change)
        {
            if (!_active || change.PropertyId != _descriptor.Id ||
                (change.Scope & ObserveScope.Editor) == 0)
                return;
            var dispatcher = _field.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                try
                {
                    dispatcher.Post(MarkDirtyAndSynchronize);
                }
                catch (ObjectDisposedException)
                {
                    Deactivate();
                }
                return;
            }
            MarkDirtyAndSynchronize();
        }

        /// <summary>Marks the field stale and synchronizes immediately when it is active.</summary>
        private void MarkDirtyAndSynchronize()
        {
            _dirty = true;
            SynchronizeIfDirty();
            if (_dirty)
                _markDirty();
        }

        /// <summary>Reconciles a deferred external change after the editor loses focus.</summary>
        private void SynchronizeAfterEdit()
        {
            if (!_dirty)
                return;
            SynchronizeIfDirty();
            if (_dirty)
                _markDirty();
        }
    }

    /// <summary>Stores one retained node-specific Inspector view.</summary>
    /// <param name="Children">Constructed controls.</param>
    /// <param name="RefreshBindings">Value refresh callbacks associated with the controls.</param>
    /// <param name="ScriptBindings">Generated script-property subscriptions.</param>
    private readonly record struct CachedInspectorView(
        UIElement[] Children,
        Func<bool>[] RefreshBindings,
        ScriptFieldBinding[] ScriptBindings);

    /// <summary>Identifies one Inspector script-title reorder operation.</summary>
    /// <param name="Owner">Component owner.</param>
    /// <param name="Component">Exact script component being moved.</param>
    private sealed record ScriptComponentDragData(Node Owner, ScriptComponent Component);
}
