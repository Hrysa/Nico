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
    private MaterialProperties? _resolvedMaterial;
    private bool _scriptBindingsDirty;
    private bool _modelBindingsDirty;
    private Node? _subscribedNode;

    /// <summary>Gets or sets the editor display-name resolver for attached script assets.</summary>
    public Func<AssetId, string?>? ResolveScriptName { get; set; }

    /// <summary>Gets or sets the resolver for compiled script types in edit mode.</summary>
    public Func<AssetId, Type?>? ResolveScriptType { get; set; }

    /// <summary>Gets or sets the resolver for live script instances in play mode.</summary>
    public Func<ScriptComponent, SceneScript?>? ResolveScriptInstance { get; set; }

    /// <summary>Gets or sets the resolver for a mesh instance's effective material values.</summary>
    public Func<MeshInstance3D, MaterialProperties>? ResolveMaterial { get; set; }

    /// <summary>Gets or sets the display-name resolver for a mesh material assignment.</summary>
    public Func<MeshInstance3D, string>? ResolveMaterialName { get; set; }

    /// <summary>Gets or sets the display-name resolver for standalone animation assignments.</summary>
    public Func<AssetReference?, string>? ResolveAnimationName { get; set; }

    /// <summary>Gets or sets the resolver for a mesh's live preview/play animation controller.</summary>
    public Func<MeshInstance3D, AnimationController?>? ResolveAnimationController { get; set; }

    /// <summary>Gets the node currently displayed by the Inspector.</summary>
    public Node? InspectedNode { get; private set; }

    /// <summary>Gets the exact component focused by Scene preview picking.</summary>
    public Component? FocusedComponent { get; private set; }

    /// <summary>Gets the active Inspector edit-form scope.</summary>
    public UIEditForm EditForm => _editForm;

    /// <summary>Occurs after an Inspector field changes the selected node.</summary>
    public event Action<Node>? NodeChanged;

    /// <summary>Occurs after the Inspector changes the selected node's displayed name.</summary>
    public event Action<Node>? NodeNameChanged;

    /// <summary>Occurs after live animation preview state changes without authored data changes.</summary>
    public event Action<MeshInstance3D>? AnimationPreviewChanged;

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
        Bind(null);
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
            ResolveBoundMaterial(node);
            RefreshValues();
            return;
        }

        UnsubscribeFromNode();
        InspectedNode = node;
        FocusedComponent = focusedComponent;
        DeactivateScriptBindings();
        _editForm.Clear();
        ClearChildren();
        _refreshBindings.Clear();
        _scriptBindings.Clear();
        ResolveBoundMaterial(node);
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
        if (node is MeshInstance3D meshInstance)
        {
            AddMaterialSection(meshInstance, 236f);
            scriptY = 464f;
            if (meshInstance.GetComponent<AnimatorComponent>() is { } animator)
                scriptY = AddAnimatorSection(meshInstance, animator, scriptY);
        }

        scriptY = AddPhysicsSections(node, scriptY);
        AddScriptSections(node, scriptY);
        CacheCurrentView(node);
        SubscribeToNode(node);
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
        AddChild(CreateLabel(12f, y, Width - 24f, 26f, "Rigid Body", _theme.TextPrimary));
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
        AddChild(CreateLabel(12f, y, Width - 24f, 26f,
            GetColliderDisplayName(collider) +
            (ReferenceEquals(collider, FocusedComponent) ? " (Selected)" : string.Empty),
            _theme.TextPrimary));
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
                AddAssetReferenceRow("Mesh", prefix + "Mesh", row, () => mesh.Mesh,
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
                    () => terrain.TerrainData, value => terrain.TerrainData = value);
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

    /// <summary>Adds editable playback settings for one animator component.</summary>
    /// <param name="instance">Animated mesh instance.</param>
    /// <param name="animator">Animator component to edit.</param>
    /// <param name="y">Section top.</param>
    /// <returns>Top position available for the following section.</returns>
    private float AddAnimatorSection(MeshInstance3D instance, AnimatorComponent animator, float y)
    {
        var liveController = ResolveAnimationController?.Invoke(instance);
        AddChild(CreateLabel(12f, y, Width - 24f, 26f,
            "Animator", _theme.TextPrimary));
        AddChild(CreateLabel(12f, y + 30f, 66f, 30f,
            "Set", _theme.TextSecondary));
        var source = new TextField(Width - 90f, 30f, _theme)
        {
            Name = "AnimatorSource",
            Text = ResolveAnimationName?.Invoke(
                animator.AnimationSet ?? animator.AnimationSource) ??
                (animator.AnimationSet ?? animator.AnimationSource)?.ToString() ??
                "Embedded in mesh",
            IsReadOnly = true,
            Margin = new Thickness(78f, y + 30f, 0f, 0f)
        };
        AddChild(source);
        AddChild(CreateLabel(12f, y + 68f, 66f, 30f,
            "Clip", _theme.TextSecondary));
        if (liveController is not null && liveController.Resource.Animations.Count > 0)
        {
            var choices = new string[liveController.Resource.Animations.Count + 1];
            choices[0] = "(First Clip)";
            var selectedIndex = 0;
            for (var index = 0; index < liveController.Resource.Animations.Count; index++)
            {
                choices[index + 1] = liveController.Resource.Animations[index].Name;
                if (string.Equals(animator.DefaultClip, choices[index + 1],
                        StringComparison.Ordinal))
                    selectedIndex = index + 1;
            }
            var clipSelector = new ComboBox(Width - 90f, 30f, _theme)
            {
                Name = "AnimatorClip",
                Margin = new Thickness(78f, y + 68f, 0f, 0f)
            };
            clipSelector.SetItems(choices);
            clipSelector.Select(selectedIndex);
            clipSelector.SelectionChanged += (index, value) =>
            {
                animator.DefaultClip = index <= 0 ? null : value;
                if (InspectedNode is { } node)
                    NodeChanged?.Invoke(node);
            };
            AddChild(clipSelector);
        }
        else
        {
            var clip = new TextField(Width - 90f, 30f, _theme)
            {
                Name = "AnimatorClip",
                Text = animator.DefaultClip ?? string.Empty,
                Placeholder = "First imported clip",
                UpdateTrigger = TextUpdateTrigger.Commit,
                Margin = new Thickness(78f, y + 68f, 0f, 0f)
            };
            clip.ValueUpdateRequested += value =>
            {
                animator.DefaultClip = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (InspectedNode is { } node)
                    NodeChanged?.Invoke(node);
            };
            RegisterRefresh(clip, () => animator.DefaultClip ?? string.Empty);
            _editForm.Register(clip);
            AddChild(clip);
        }
        AddChild(CreateLabel(12f, y + 106f, 66f, 30f,
            "Speed", _theme.TextSecondary));
        var speed = new TextField(Width - 90f, 30f, _theme)
        {
            Name = "AnimatorSpeed",
            Text = Format(animator.Speed),
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = ValidateFloat,
            Margin = new Thickness(78f, y + 106f, 0f, 0f)
        };
        speed.ValueUpdateRequested += value =>
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed))
                return;
            animator.Speed = parsed;
            if (InspectedNode is { } node)
                NodeChanged?.Invoke(node);
        };
        RegisterRefresh(speed, () => Format(animator.Speed));
        _editForm.Register(speed);
        AddChild(speed);
        AddChild(CreateLabel(12f, y + 144f, 66f, 30f,
            "Fade", _theme.TextSecondary));
        var fade = new TextField(Width - 90f, 30f, _theme)
        {
            Name = "AnimatorDefaultFadeDuration",
            Text = Format(animator.DefaultFadeDuration),
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = text => ValidateConstrainedFloat(text, false, true),
            Margin = new Thickness(78f, y + 144f, 0f, 0f)
        };
        fade.ValueUpdateRequested += value =>
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed) || parsed < 0f)
                return;
            animator.DefaultFadeDuration = parsed;
            if (InspectedNode is { } node)
                NodeChanged?.Invoke(node);
        };
        RegisterRefresh(fade, () => Format(animator.DefaultFadeDuration));
        _editForm.Register(fade);
        AddChild(fade);
        var play = new ToggleButton((Width - 28f) * 0.5f, 30f,
            "Play Automatically", _theme)
        {
            Name = "AnimatorPlayAutomatically",
            IsChecked = animator.PlayAutomatically,
            Margin = new Thickness(12f, y + 182f, 0f, 0f)
        };
        play.CheckedChanged += value =>
        {
            animator.PlayAutomatically = value;
            if (InspectedNode is { } node)
                NodeChanged?.Invoke(node);
        };
        AddChild(play);
        var loop = new ToggleButton((Width - 28f) * 0.5f, 30f,
            "Loop", _theme)
        {
            Name = "AnimatorLoop",
            IsChecked = animator.Loop,
            Margin = new Thickness(16f + (Width - 28f) * 0.5f, y + 182f, 0f, 0f)
        };
        loop.CheckedChanged += value =>
        {
            animator.Loop = value;
            if (InspectedNode is { } node)
                NodeChanged?.Invoke(node);
        };
        AddChild(loop);
        var bottom = y + 224f;
        var controller = liveController;
        var previewState = ResolvePreviewState(controller, animator.DefaultClip);
        if (controller is null || previewState is null)
            return bottom;
        AddChild(CreateLabel(12f, y + 220f, 66f, 30f,
            "State", _theme.TextSecondary));
        var stateName = new TextField(Width - 90f, 30f, _theme)
        {
            Name = "AnimatorRuntimeState",
            Text = controller.Current?.Key ?? previewState.Key,
            IsReadOnly = true,
            Margin = new Thickness(78f, y + 220f, 0f, 0f)
        };
        RegisterRefresh(stateName, () => controller.Current?.Key ?? previewState.Key);
        AddChild(stateName);
        AddChild(CreateLabel(12f, y + 258f, 66f, 30f,
            "Time", _theme.TextSecondary));
        var previewTime = new TextField(Width - 164f, 30f, _theme)
        {
            Name = "AnimatorPreviewNormalizedTime",
            Text = Format(previewState.NormalizedTime),
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = text => ValidateConstrainedFloat(text, false, true),
            Margin = new Thickness(78f, y + 258f, 0f, 0f)
        };
        previewTime.ValueUpdateRequested += value =>
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed) || parsed < 0f)
                return;
            if (!previewState.IsCurrent)
            {
                controller.Play(previewState.Key, 0f);
                previewState.IsPlaying = false;
            }
            previewState.NormalizedTime = Math.Clamp(parsed, 0f, 1f);
            AnimationPreviewChanged?.Invoke(instance);
        };
        RegisterRefresh(previewTime, () => Format(previewState.NormalizedTime));
        _editForm.Register(previewTime);
        AddChild(previewTime);
        var previewPlaying = new ToggleButton(66f, 30f, "Play", _theme)
        {
            Name = "AnimatorPreviewPlaying",
            IsChecked = previewState.IsPlaying,
            Margin = new Thickness(Width - 78f, y + 258f, 0f, 0f)
        };
        previewPlaying.CheckedChanged += value =>
        {
            if (value && !previewState.IsCurrent)
                controller.Play(previewState.Key, 0f);
            previewState.IsPlaying = value;
            AnimationPreviewChanged?.Invoke(instance);
        };
        AddChild(previewPlaying);
        AddChild(CreateLabel(12f, y + 296f, 66f, 30f,
            "Blend", _theme.TextSecondary));
        var blendStatus = new TextField(Width - 90f, 30f, _theme)
        {
            Name = "AnimatorRuntimeBlend",
            Text = FormatAnimationState(previewState),
            IsReadOnly = true,
            Margin = new Thickness(78f, y + 296f, 0f, 0f)
        };
        RegisterRefresh(blendStatus, () => FormatAnimationState(previewState));
        AddChild(blendStatus);
        return y + 338f;
    }

    /// <summary>Formats live playback and cross-fade diagnostics without changing authored data.</summary>
    /// <param name="state">Runtime state displayed by the Inspector.</param>
    /// <returns>Compact speed, weight, target, and remaining-fade details.</returns>
    private static string FormatAnimationState(AnimationState state) =>
        $"x{Format(state.Speed)}  {Format(state.Weight)}→{Format(state.TargetWeight)}  " +
        $"{Format(state.FadeRemaining)}s";

    /// <summary>Resolves the current or authored-default state for live Inspector preview.</summary>
    /// <param name="controller">Optional live controller.</param>
    /// <param name="defaultClip">Optional authored default key.</param>
    /// <returns>A reusable preview state, or null when no clip exists.</returns>
    private static AnimationState? ResolvePreviewState(
        AnimationController? controller, string? defaultClip)
    {
        if (controller is null)
            return null;
        if (controller.Current is { } current)
            return current;
        if (defaultClip is not null && controller.TryGet(defaultClip, out var authored))
            return authored;
        return controller.Resource.Animations.Count > 0
            ? controller.GetOrCreate(controller.Resource.Animations[0].Name) : null;
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
            var scriptField = new TextField(Width - 24f, 30f, _theme)
            {
                Name = currentIndex == 0 ? "ScriptAssetField" : $"ScriptAssetField{currentIndex}",
                Text = GetScriptDisplayName(component.ScriptId),
                IsReadOnly = true,
                Margin = new Thickness(12f, rowY, 0f, 0f)
            };
            AddChild(scriptField);
            rowY += 38f;
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
        if (scriptIndex != 0)
            return;
        AddChild(new TextField(Width - 24f, 30f, _theme)
        {
            Name = "ScriptAssetField",
            Placeholder = "No script attached",
            IsReadOnly = true,
            Margin = new Thickness(12f, rowY, 0f, 0f)
        });
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
                UpdateTrigger = TextUpdateTrigger.Commit,
                Validator = ValidateFloat,
                Margin = new Thickness(12f + labelWidth + index * (fieldWidth + spacing), y, 0f, 0f)
            };
            field.ValueUpdateRequested += text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var component))
                    return;
                var material = GetOrCreateOverride(instance);
                material.BaseColor = WithComponent(material.BaseColor, componentIndex,
                    Math.Clamp(component, 0f, 1f));
            };
            field.EditCommitted += _ => NodeChanged?.Invoke(instance);
            _editForm.Register(field);
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
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = ValidateFloat,
            Margin = new Thickness(92f, y, 0f, 0f)
        };
        field.ValueUpdateRequested += text =>
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var value))
                return;
            write(GetOrCreateOverride(instance), value);
        };
        field.EditCommitted += _ => NodeChanged?.Invoke(instance);
        _editForm.Register(field);
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
        var dropTarget = Children.OfType<TextField>()
            .FirstOrDefault(element => element.Name == "ScriptAssetField");
        if (dropTarget is not null)
            dropTarget.Text = GetScriptDisplayName(scriptId);
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

    /// <summary>Assigns a standalone skeletal-animation artifact to the inspected mesh.</summary>
    /// <param name="animation">Standalone animation sub-asset.</param>
    /// <returns>True when an inspected mesh received the assignment.</returns>
    public bool AssignAnimation(AssetReference animation)
    {
        if (InspectedNode is not MeshInstance3D instance)
            return false;
        var animator = instance.GetComponent<AnimatorComponent>();
        if (animator is null)
        {
            animator = new AnimatorComponent();
            instance.AddComponent(animator);
        }
        animator.AnimationSet = null;
        animator.AnimationSource = animation;
        animator.DefaultClip = null;
        NodeChanged?.Invoke(instance);
        _cachedViews.Remove(instance);
        InspectedNode = null;
        Bind(instance);
        return true;
    }

    /// <summary>Assigns a project-owned animation set to the inspected mesh.</summary>
    /// <param name="animationSet">Animation-set artifact.</param>
    /// <returns>True when an inspected mesh received the assignment.</returns>
    public bool AssignAnimationSet(AssetReference animationSet)
    {
        if (InspectedNode is not MeshInstance3D instance)
            return false;
        var animator = instance.GetComponent<AnimatorComponent>();
        if (animator is null)
        {
            animator = new AnimatorComponent();
            instance.AddComponent(animator);
        }
        animator.AnimationSource = null;
        animator.AnimationSet = animationSet;
        animator.DefaultClip = null;
        NodeChanged?.Invoke(instance);
        _cachedViews.Remove(instance);
        InspectedNode = null;
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

    /// <summary>Adds an editable explicit asset UUID with optional hash-delimited subasset.</summary>
    /// <param name="label">Displayed label.</param><param name="name">Field name.</param>
    /// <param name="y">Row position.</param><param name="read">Current reference reader.</param>
    /// <param name="apply">Reference writer.</param>
    private void AddAssetReferenceRow(string label, string name, float y,
        Func<AssetReference?> read, Action<AssetReference?> apply)
    {
        AddChild(CreateLabel(12f, y, 66f, 30f, label, _theme.TextSecondary));
        var field = new TextField(Width - 90f, 30f, _theme)
        {
            Name = name,
            Text = read()?.ToString() ?? string.Empty,
            Placeholder = "Required asset UUID[#subasset]",
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = text => TryParseAssetReference(text, out _) ? null :
                "Enter an asset UUID with an optional #subasset.",
            Margin = new Thickness(78f, y, 0f, 0f)
        };
        field.ValueUpdateRequested += text =>
        {
            if (!TryParseAssetReference(text, out var reference))
                return;
            apply(reference);
            if (InspectedNode is { } inspected)
                NodeChanged?.Invoke(inspected);
        };
        RegisterRefresh(field, () => read()?.ToString() ?? string.Empty);
        _editForm.Register(field);
        AddChild(field);
    }

    /// <summary>Parses an explicit asset reference entered in diagnostic form.</summary>
    /// <param name="text">UUID followed by an optional hash and subasset.</param>
    /// <param name="reference">Parsed reference.</param><returns>True when valid.</returns>
    private static bool TryParseAssetReference(string text, out AssetReference? reference)
    {
        var separator = text.IndexOf('#');
        var assetText = separator < 0 ? text : text[..separator];
        var subAsset = separator < 0 ? null : text[(separator + 1)..];
        if (!AssetId.TryParse(assetText.Trim(), out var asset) ||
            subAsset is not null && string.IsNullOrWhiteSpace(subAsset))
        {
            reference = null;
            return false;
        }
        reference = new AssetReference(asset, subAsset?.Trim());
        return true;
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
    private void AddVectorRow(
        string label,
        string namePrefix,
        float y,
        Func<Vector3> read,
        Action<Vector3> apply,
        bool radiansAsDegrees,
        bool positive = false)
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
                Validator = text => positive
                    ? ValidateConstrainedFloat(text, true, false)
                    : ValidateFloat(text),
                Margin = new Thickness(12f + labelWidth + index * (fieldWidth + spacing),
                    y, 0f, 0f)
            };
            field.ValueUpdateRequested += text =>
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var component) || positive && component <= 0f)
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
            PaddingLeft = 0f,
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
}
