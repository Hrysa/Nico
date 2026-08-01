using Editor;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIEventRouterTests
{
    /// <summary>Verifies that the topmost child receives pointer state.</summary>
    [Fact]
    public void MovePointer_OverlappingChildren_HoversTopmostChild()
    {
        var root = new Panel(0f, 0f, 100f, 100f, Color.Black);
        var first = new Panel(0f, 0f, 100f, 100f, Color.Black);
        var second = new Panel(0f, 0f, 100f, 100f, Color.Black);
        root.AddChild(first);
        root.AddChild(second);
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new(50f, 50f));

        Assert.Same(second, router.HoveredElement);
        Assert.True(second.IsHovered);
        Assert.False(first.IsHovered);
    }

    /// <summary>Verifies that a press and release dispatches one click.</summary>
    [Fact]
    public void Release_AfterPress_InvokesClick()
    {
        var button = new Button(0f, 0f, 100f, 100f, "Test", Color.Black);
        var clicks = 0;
        button.Click += () => clicks++;
        var router = new UIEventRouter(button, () => { });
        router.MovePointer(new(50f, 50f));
        router.Press();

        router.Release(invokeClick: true);

        Assert.Equal(1, clicks);
        Assert.False(button.IsPressed);
    }
}
