namespace Engine.UI;

/// <summary>Base class for retained controls supporting resources, styles, and visual templates.</summary>
public class Control : Box
{
    private IUIStyle? _style;
    private IUIControlTemplate? _template;
    private UIElement? _templateRoot;

    /// <summary>Creates a retained control.</summary>
    /// <param name="width">Optional explicit width.</param>
    /// <param name="height">Optional explicit height.</param>
    public Control(float width = 0f, float height = 0f)
        : base(width, height)
    {
    }

    /// <summary>Gets or sets an optional named style variant resolved through inherited resources.</summary>
    public string? StyleKey { get; set; }

    /// <summary>Gets or sets an explicit typed style.</summary>
    public IUIStyle? Style
    {
        get => _style;
        set
        {
            if (ReferenceEquals(_style, value))
                return;
            _style = value;
            if (value is not null)
                value.Apply(this);
        }
    }

    /// <summary>Gets or sets the factory producing this control's optional visual presentation.</summary>
    public IUIControlTemplate? Template
    {
        get => _template;
        set
        {
            if (ReferenceEquals(_template, value))
                return;
            _template = value;
            ApplyTemplate();
        }
    }

    /// <summary>Gets the visual root created by the active template.</summary>
    public UIElement? TemplateRoot => _templateRoot;

    /// <summary>Resolves and applies an explicit or inherited typed style.</summary>
    /// <returns>True when a compatible style was applied.</returns>
    public bool ApplyStyle()
    {
        if (Style is { } explicitStyle)
        {
            explicitStyle.Apply(this);
            return true;
        }
        for (Type? type = GetType(); type is not null && typeof(UIElement).IsAssignableFrom(type);
             type = type.BaseType)
        {
            if (!TryFindResource(new UIStyleResourceKey(type, StyleKey), out IUIStyle? style)
                || style is null)
                continue;
            style.Apply(this);
            return true;
        }
        return false;
    }

    /// <summary>Rebuilds the optional template-owned visual root.</summary>
    /// <returns>True when a template root is active.</returns>
    public bool ApplyTemplate()
    {
        if (_templateRoot is not null)
        {
            RemoveVisualChild(_templateRoot);
            _templateRoot = null;
        }
        if (_template is null)
            return false;
        if (!_template.TargetType.IsAssignableFrom(GetType()))
            throw new InvalidOperationException(
                $"Template for {_template.TargetType.Name} cannot build {GetType().Name}.");
        var root = _template.Build(this);
        if (root.Parent is not null)
            throw new InvalidOperationException("A control template must return an unparented element.");
        _templateRoot = root;
        AddVisualChild(root);
        return true;
    }

    /// <inheritdoc/>
    protected override System.Numerics.Vector2 MeasureOverride(System.Numerics.Vector2 availableSize)
    {
        if (_templateRoot is null)
            return base.MeasureOverride(availableSize);
        _templateRoot.Measure(availableSize);
        return _templateRoot.DesiredSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(System.Numerics.Vector2 contentSize)
    {
        if (_templateRoot is null)
        {
            base.ArrangeOverride(contentSize);
            return;
        }
        _templateRoot.Arrange(System.Numerics.Vector2.Zero, contentSize);
    }
}
