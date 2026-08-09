using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class HudRootTests
{
    /// <summary>Verifies content replacement publishes both roots and preserves overlay behavior.</summary>
    [Fact]
    public void Content_Replacement_PublishesPreparedTree()
    {
        var hud = new HudRoot();
        var previous = hud.Content;
        var replacement = new Panel();
        UIElement? publishedPrevious = null;
        UIElement? publishedCurrent = null;
        hud.ContentChanged += (oldContent, newContent) =>
        {
            publishedPrevious = oldContent;
            publishedCurrent = newContent;
        };

        hud.Content = replacement;

        Assert.Same(previous, publishedPrevious);
        Assert.Same(replacement, publishedCurrent);
        Assert.Same(replacement, hud.Content);
        Assert.True(replacement.IsOverlay);
        Assert.True(replacement.ClipToBounds);
    }

    /// <summary>Verifies a retained subtree cannot be shared between a HUD and another parent.</summary>
    [Fact]
    public void Content_ParentedTree_ThrowsInvalidOperationException()
    {
        var parent = new Panel();
        var child = new Panel();
        parent.AddChild(child);
        var hud = new HudRoot();

        Assert.Throws<InvalidOperationException>(() => hud.Content = child);
    }
}
