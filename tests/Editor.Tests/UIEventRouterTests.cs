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
        var root = new Panel(Color.Black, 100f, 100f);
        var first = new Panel(Color.Black, 100f, 100f);
        var second = new Panel(Color.Black, 100f, 100f);
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
        var button = new Button(100f, 100f, "Test", Color.Black);
        var clicks = 0;
        button.Click += () => clicks++;
        var router = new UIEventRouter(button, () => { });
        router.MovePointer(new(50f, 50f));
        router.Press();

        router.Release(invokeClick: true);

        Assert.Equal(1, clicks);
        Assert.False(button.IsPressed);
    }

    /// <summary>Verifies releasing outside a pressed element clears its captured press state.</summary>
    [Fact]
    public void Release_AfterPointerLeavesPressedElement_ClearsOriginalPressWithoutClick()
    {
        var root = new Panel(Color.Black, 200f, 100f);
        var button = new Button(100f, 100f, "Test", Color.Black);
        var clicks = 0;
        button.Click += () => clicks++;
        root.AddChild(button);
        var router = new UIEventRouter(root, () => { });
        router.MovePointer(new(50f, 50f));
        router.Press();

        router.MovePointer(new(150f, 50f));
        router.Release(invokeClick: true);

        Assert.False(button.IsPressed);
        Assert.Equal(0, clicks);

        router.MovePointer(new(50f, 50f));
        router.Press();
        Assert.True(button.IsPressed);
    }
}
