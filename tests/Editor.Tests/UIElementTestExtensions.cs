using Engine.UI;

namespace Editor.Tests;

/// <summary>Test helpers for inspecting composed UI trees.</summary>
internal static class UIElementTestExtensions
{
    /// <summary>Enumerates all descendants beneath one UI element.</summary>
    /// <param name="element">Subtree root.</param>
    /// <returns>Descendants in depth-first order.</returns>
    public static IEnumerable<UIElement> Descendants(this UIElement element)
    {
        foreach (var child in element.Children.OfType<UIElement>())
        {
            yield return child;
            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }
}
