namespace Engine.UI;

/// <summary>Identifies a typed style resource with an optional application key.</summary>
/// <param name="TargetType">Element type styled by the resource.</param>
/// <param name="Key">Optional named style variant.</param>
public readonly record struct UIStyleResourceKey(Type TargetType, string? Key = null);

/// <summary>Stores host- or subtree-scoped UI resources with explicit ownership.</summary>
public sealed class UIResourceDictionary
{
    private readonly Dictionary<object, object> _values = [];

    /// <summary>Gets the number of locally stored resources.</summary>
    public int Count => _values.Count;

    /// <summary>Adds or replaces one resource.</summary>
    /// <param name="key">Non-null lookup key.</param>
    /// <param name="value">Non-null resource value.</param>
    public void Set(object key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
    }

    /// <summary>Adds or replaces one typed style resource.</summary>
    /// <param name="style">Typed style.</param>
    /// <param name="key">Optional named style variant.</param>
    public void SetStyle(IUIStyle style, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        Set(new UIStyleResourceKey(style.TargetType, key), style);
    }

    /// <summary>Attempts to retrieve one untyped resource.</summary>
    /// <param name="key">Lookup key.</param>
    /// <param name="value">Stored value when present.</param>
    /// <returns>True when the resource exists locally.</returns>
    public bool TryGet(object key, out object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _values.TryGetValue(key, out value);
    }

    /// <summary>Attempts to retrieve and type-check one resource.</summary>
    /// <typeparam name="T">Required resource type.</typeparam>
    /// <param name="key">Lookup key.</param>
    /// <param name="value">Typed value when present.</param>
    /// <returns>True when a compatible resource exists locally.</returns>
    public bool TryGet<T>(object key, out T? value) where T : class
    {
        if (TryGet(key, out var resource) && resource is T typed)
        {
            value = typed;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Removes one local resource.</summary>
    /// <param name="key">Lookup key.</param>
    /// <returns>True when a resource was removed.</returns>
    public bool Remove(object key) => _values.Remove(key);

    /// <summary>Removes every local resource.</summary>
    public void Clear() => _values.Clear();
}

/// <summary>Applies reusable typed property configuration to retained elements.</summary>
public interface IUIStyle
{
    /// <summary>Gets the element type accepted by this style.</summary>
    Type TargetType { get; }

    /// <summary>Applies the style to one compatible element.</summary>
    /// <param name="element">Element receiving style values.</param>
    void Apply(UIElement element);
}

/// <summary>Composes strongly typed reusable style setters.</summary>
/// <typeparam name="TElement">Element type configured by this style.</typeparam>
public sealed class UIStyle<TElement> : IUIStyle where TElement : UIElement
{
    private readonly List<Action<TElement>> _setters = [];

    /// <summary>Gets or sets an optional base style applied before local setters.</summary>
    public UIStyle<TElement>? BasedOn { get; set; }

    /// <inheritdoc/>
    public Type TargetType => typeof(TElement);

    /// <summary>Adds one strongly typed style setter.</summary>
    /// <param name="setter">Property assignment or equivalent idempotent configuration.</param>
    /// <returns>This style for fluent construction.</returns>
    public UIStyle<TElement> Add(Action<TElement> setter)
    {
        ArgumentNullException.ThrowIfNull(setter);
        _setters.Add(setter);
        return this;
    }

    /// <summary>Applies base and local setters to one typed element.</summary>
    /// <param name="element">Element receiving style values.</param>
    public void Apply(TElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        BasedOn?.Apply(element);
        for (var index = 0; index < _setters.Count; index++)
            _setters[index](element);
    }

    /// <inheritdoc/>
    void IUIStyle.Apply(UIElement element)
    {
        if (element is not TElement typed)
            throw new ArgumentException(
                $"Style for {typeof(TElement).Name} cannot be applied to {element.GetType().Name}.",
                nameof(element));
        Apply(typed);
    }
}

/// <summary>Creates a visual root for a compatible control.</summary>
public interface IUIControlTemplate
{
    /// <summary>Gets the control type accepted by this template.</summary>
    Type TargetType { get; }

    /// <summary>Builds one unparented visual root.</summary>
    /// <param name="control">Control requesting its presentation.</param>
    /// <returns>New unparented retained visual root.</returns>
    UIElement Build(Control control);
}

/// <summary>Strongly typed factory for a control's retained visual presentation.</summary>
/// <typeparam name="TControl">Compatible control type.</typeparam>
public sealed class UIControlTemplate<TControl> : IUIControlTemplate where TControl : Control
{
    private readonly Func<TControl, UIElement> _factory;

    /// <summary>Creates a typed control template.</summary>
    /// <param name="factory">Factory returning a new unparented visual root.</param>
    public UIControlTemplate(Func<TControl, UIElement> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public Type TargetType => typeof(TControl);

    /// <summary>Builds a retained visual root for one typed control.</summary>
    /// <param name="control">Control requesting presentation.</param>
    /// <returns>New unparented visual root.</returns>
    public UIElement Build(TControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return _factory(control) ?? throw new InvalidOperationException("A control template returned null.");
    }

    /// <inheritdoc/>
    UIElement IUIControlTemplate.Build(Control control)
    {
        if (control is not TControl typed)
            throw new ArgumentException(
                $"Template for {typeof(TControl).Name} cannot build {control.GetType().Name}.",
                nameof(control));
        return Build(typed);
    }
}

/// <summary>Creates a retained element for a compatible application data item.</summary>
public interface IUIDataTemplate
{
    /// <summary>Gets the data type accepted by this template.</summary>
    Type DataType { get; }

    /// <summary>Builds retained content for one compatible data item.</summary>
    /// <param name="item">Application data item.</param>
    /// <returns>New unparented retained content.</returns>
    UIElement Build(object item);
}

/// <summary>Strongly typed factory for retained application-data presentation.</summary>
/// <typeparam name="TItem">Compatible application data type.</typeparam>
public sealed class UIDataTemplate<TItem> : IUIDataTemplate
{
    private readonly Func<TItem, UIElement> _factory;

    /// <summary>Creates a typed data template.</summary>
    /// <param name="factory">Factory returning a new unparented retained element.</param>
    public UIDataTemplate(Func<TItem, UIElement> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public Type DataType => typeof(TItem);

    /// <summary>Builds retained content for one typed data item.</summary>
    /// <param name="item">Application data item.</param>
    /// <returns>New unparented retained content.</returns>
    public UIElement Build(TItem item) =>
        _factory(item) ?? throw new InvalidOperationException("A data template returned null.");

    /// <inheritdoc/>
    UIElement IUIDataTemplate.Build(object item)
    {
        if (item is not TItem typed)
            throw new ArgumentException(
                $"Data template for {typeof(TItem).Name} cannot build {item.GetType().Name}.",
                nameof(item));
        return Build(typed);
    }
}
