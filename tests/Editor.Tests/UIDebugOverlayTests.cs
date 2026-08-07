using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies retained UI diagnostic visualization.</summary>
public sealed class UIDebugOverlayTests
{
    /// <summary>Verifies pointer target changes invalidate cached diagnostic commands.</summary>
    [Fact]
    public void HitTargetChange_RebuildsOverlayHighlight()
    {
        var root = new Canvas { Width = 100f, Height = 100f };
        var button = new Button(30f, 20f, "Hit");
        root.Add(button, new Vector2(10f, 15f));
        root.Measure(new Vector2(100f, 100f));
        root.Arrange(Vector2.Zero, new Vector2(100f, 100f));
        var router = new UIEventRouter(root, () => { });
        using var overlay = new UIDebugOverlay(root, router)
        {
            Width = 100f,
            Height = 100f,
            Options = UIDebugOverlayOptions.Bounds | UIDebugOverlayOptions.HitTarget
        };

        var initial = overlay.BuildDrawList();
        var initialGeneration = initial.Generation;
        var initialCommandCount = initial.Commands.Count;
        Assert.True(initialCommandCount >= 8);

        router.MovePointer(new Vector2(20f, 20f));
        var highlighted = overlay.BuildDrawList();

        Assert.Equal(initialCommandCount + 4, highlighted.Commands.Count);
        Assert.True(highlighted.Generation > initialGeneration);
        Assert.All(highlighted.Commands.Skip(initialCommandCount),
            command => Assert.Equal(Color.Yellow, command.Color));
    }

    /// <summary>Verifies nested clipping diagnostics use the effective intersected rectangle.</summary>
    [Fact]
    public void NestedClips_DrawEffectiveIntersection()
    {
        var root = new Canvas { Width = 100f, Height = 100f, ClipToBounds = true };
        var child = new Panel(Color.Black, 50f, 40f) { ClipToBounds = true };
        root.Add(child, new Vector2(80f, 10f));
        root.Measure(new Vector2(100f, 100f));
        root.Arrange(Vector2.Zero, new Vector2(100f, 100f));
        var router = new UIEventRouter(root, () => { });
        using var overlay = new UIDebugOverlay(root, router)
        {
            Width = 100f,
            Height = 100f,
            Options = UIDebugOverlayOptions.Clips
        };

        var commands = overlay.BuildDrawList().Commands;

        Assert.Equal(8, commands.Count);
        Assert.Contains(commands, command => command.Left == 100f && command.Top == 10f &&
            command.Right == 100f && command.Bottom == 50f);
    }
}
