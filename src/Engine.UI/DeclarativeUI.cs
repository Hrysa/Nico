using Engine.Graphics;

namespace Engine.UI;

/// <summary>Provides concise declarative factories for retained UI trees.</summary>
public static class UI
{
    /// <summary>Creates a configured horizontal flex container from a declarative child list.</summary>
    /// <param name="children">Children in visual order.</param>
    /// <param name="backgroundColor">Optional painted background.</param>
    /// <param name="justifyContent">Main-axis free-space distribution.</param>
    /// <param name="alignItems">Cross-axis item alignment.</param>
    /// <param name="gap">Uniform item and line gap.</param>
    /// <param name="wrap">Line wrapping policy.</param>
    /// <returns>The constructed row.</returns>
    public static FlexPanel Row(
        UIElement[] children,
        Color? backgroundColor = null,
        FlexJustify justifyContent = FlexJustify.Start,
        FlexAlignment alignItems = FlexAlignment.Stretch,
        float gap = 0f,
        FlexWrap wrap = FlexWrap.NoWrap) =>
        Flex(FlexDirection.Row, backgroundColor, justifyContent, alignItems, gap, wrap, children);

    /// <summary>Creates a horizontal flex container containing the supplied children.</summary>
    /// <param name="backgroundColor">Optional painted background.</param>
    /// <param name="children">Children in visual order.</param>
    /// <returns>The constructed row.</returns>
    public static FlexPanel Row(Color? backgroundColor = null, params UIElement[] children) =>
        Flex(FlexDirection.Row, backgroundColor, FlexJustify.Start,
            FlexAlignment.Stretch, 0f, FlexWrap.NoWrap, children);

    /// <summary>Creates a configured vertical flex container from a declarative child list.</summary>
    /// <param name="children">Children in visual order.</param>
    /// <param name="backgroundColor">Optional painted background.</param>
    /// <param name="justifyContent">Main-axis free-space distribution.</param>
    /// <param name="alignItems">Cross-axis item alignment.</param>
    /// <param name="gap">Uniform item and line gap.</param>
    /// <param name="wrap">Line wrapping policy.</param>
    /// <returns>The constructed column.</returns>
    public static FlexPanel Column(
        UIElement[] children,
        Color? backgroundColor = null,
        FlexJustify justifyContent = FlexJustify.Start,
        FlexAlignment alignItems = FlexAlignment.Stretch,
        float gap = 0f,
        FlexWrap wrap = FlexWrap.NoWrap) =>
        Flex(FlexDirection.Column, backgroundColor, justifyContent, alignItems, gap, wrap, children);

    /// <summary>Creates a vertical flex container containing the supplied children.</summary>
    /// <param name="backgroundColor">Optional painted background.</param>
    /// <param name="children">Children in visual order.</param>
    /// <returns>The constructed column.</returns>
    public static FlexPanel Column(Color? backgroundColor = null, params UIElement[] children) =>
        Flex(FlexDirection.Column, backgroundColor, FlexJustify.Start,
            FlexAlignment.Stretch, 0f, FlexWrap.NoWrap, children);

    /// <summary>Creates a layered container from a declarative child list.</summary>
    /// <param name="children">Layers from back to front.</param>
    /// <param name="backgroundColor">Optional painted background.</param>
    /// <returns>The constructed overlay.</returns>
    public static OverlayPanel Overlay(UIElement[] children, Color? backgroundColor = null)
    {
        var panel = new OverlayPanel(backgroundColor);
        AddChildren(panel, children);
        return panel;
    }

    /// <summary>Creates a layered container containing the supplied children.</summary>
    /// <param name="backgroundColor">Optional painted background.</param>
    /// <param name="children">Layers from back to front.</param>
    /// <returns>The constructed overlay.</returns>
    public static OverlayPanel Overlay(Color? backgroundColor = null, params UIElement[] children)
    {
        var panel = new OverlayPanel(backgroundColor);
        AddChildren(panel, children);
        return panel;
    }

    /// <summary>Adds children to an existing retained container and returns that container.</summary>
    /// <typeparam name="T">Container type.</typeparam>
    /// <param name="parent">Container receiving the children.</param>
    /// <param name="children">Children in visual order.</param>
    /// <returns>The same container for fluent composition.</returns>
    public static T WithChildren<T>(this T parent, params UIElement[] children) where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(parent);
        AddChildren(parent, children);
        return parent;
    }

    /// <summary>Sets a flex grow factor and returns the same element.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="element">Element to configure.</param>
    /// <param name="factor">Positive free-space share.</param>
    /// <returns>The same element.</returns>
    public static T Grow<T>(this T element, float factor = 1f) where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(element);
        element.FlexGrow = factor;
        return element;
    }

    /// <summary>Captures a retained element reference while leaving it inline in a declarative tree.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="element">Element to capture.</param>
    /// <param name="reference">Captured reference.</param>
    /// <returns>The same element for inline composition.</returns>
    public static T Ref<T>(T element, out T reference) where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(element);
        reference = element;
        return element;
    }

    /// <summary>Applies construction-time configuration and returns the same element.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="element">Element to configure.</param>
    /// <param name="configure">Construction-time configuration callback.</param>
    /// <returns>The configured element.</returns>
    public static T Configure<T>(this T element, Action<T> configure) where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(configure);
        configure(element);
        return element;
    }

    /// <summary>Assigns a diagnostic name and returns the same element.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="element">Element to name.</param>
    /// <param name="name">Non-empty diagnostic name.</param>
    /// <returns>The named element.</returns>
    public static T Named<T>(this T element, string name) where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrEmpty(name);
        element.Name = name;
        return element;
    }

    /// <summary>Creates one flex container and attaches its children.</summary>
    /// <param name="direction">Main-axis direction.</param>
    /// <param name="backgroundColor">Optional painted background.</param>
    /// <param name="children">Children in visual order.</param>
    /// <returns>The constructed flex panel.</returns>
    private static FlexPanel Flex(
        FlexDirection direction,
        Color? backgroundColor,
        FlexJustify justifyContent,
        FlexAlignment alignItems,
        float gap,
        FlexWrap wrap,
        UIElement[] children)
    {
        var panel = new FlexPanel(backgroundColor)
        {
            Direction = direction,
            JustifyContent = justifyContent,
            AlignItems = alignItems,
            Gap = gap,
            Wrap = wrap
        };
        AddChildren(panel, children);
        return panel;
    }

    /// <summary>Attaches a declarative child array without interface enumeration.</summary>
    /// <param name="parent">Container receiving children.</param>
    /// <param name="children">Children to attach.</param>
    private static void AddChildren(UIElement parent, UIElement[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        for (var index = 0; index < children.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(children[index]);
            parent.AddChild(children[index]);
        }
    }
}
