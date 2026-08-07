using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class RangeControlTests
{
    /// <summary>Verifies horizontal slider thumbs drag with capture and update values.</summary>
    [Fact]
    public void Slider_ThumbDrag_UpdatesValueAndReleasesCapture()
    {
        var slider = new Slider(UIOrientation.Horizontal, 100f, 20f) { Value = 0.5f };
        slider.BuildDrawList();
        var router = new UIEventRouter(slider, () => { });
        router.Press(new PointerButtonEvent(
            0, new Vector2(50f, 10f), InputPointerButton.Primary, true, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));
        Assert.Same(slider.Thumb, router.CapturedElement);

        router.RoutePointerMove(new PointerMoveEvent(
            0, new Vector2(70f, 10f), new Vector2(20f, 0f), PointerDeviceKind.Mouse,
            InputModifiers.None, PointerButtons.Primary));
        router.Release(new PointerButtonEvent(
            0, new Vector2(70f, 10f), InputPointerButton.Primary, false, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.None), true);

        Assert.True(slider.Value > 0.7f);
        Assert.Null(router.CapturedElement);
    }

    /// <summary>Verifies focused sliders support arrows, Home, and End.</summary>
    [Fact]
    public void Slider_Keyboard_ClampsAndHandlesRangeKeys()
    {
        var slider = new Slider(UIOrientation.Horizontal, 100f, 20f)
        {
            Minimum = 10f,
            Maximum = 20f,
            Value = 15f,
            SmallChange = 2f
        };
        var router = new UIEventRouter(slider, () => { });
        router.Focus(slider);

        router.RouteKey(new KeyInputEvent(InputKey.Right, true, false, InputModifiers.None));
        Assert.Equal(17f, slider.Value);
        router.RouteKey(new KeyInputEvent(InputKey.Home, true, false, InputModifiers.None));
        Assert.Equal(10f, slider.Value);
        router.RouteKey(new KeyInputEvent(InputKey.End, true, false, InputModifiers.None));
        Assert.Equal(20f, slider.Value);
    }

    /// <summary>Verifies determinate progress clamps values and paints only the resolved fraction.</summary>
    [Fact]
    public void ProgressBar_DeterminateValue_ClampsAndPaintsFraction()
    {
        var progress = new ProgressBar(100f, 10f) { Value = 0.5f };

        var commands = progress.BuildDrawList().Commands;

        Assert.Contains(commands, command => command.Color == UITheme.Dark.Accent && command.Right == 50f);
        progress.Value = 2f;
        Assert.Equal(1f, progress.Value);
    }

    /// <summary>Verifies reduced motion replaces indeterminate travel with a stable centered segment.</summary>
    [Fact]
    public void ProgressBar_ReducedMotion_UsesStableIndeterminateState()
    {
        var root = new Panel(Color.Black)
        {
            Width = 100f,
            Height = 10f,
            MotionPreference = UIMotionPreference.Reduced
        };
        var progress = new ProgressBar(100f, 10f) { IsIndeterminate = true };
        root.AddChild(progress);

        Assert.False(root.AdvanceTime(1d));
        var commands = root.BuildDrawList().Commands;

        Assert.Contains(commands, command => command.Color == UITheme.Dark.Accent
            && command.Left == 35f && command.Right == 65f);
    }

    /// <summary>Verifies clearing reduced motion resumes ordinary indeterminate animation.</summary>
    [Fact]
    public void ProgressBar_FullMotion_AdvancesIndeterminateState()
    {
        var progress = new ProgressBar(100f, 10f) { IsIndeterminate = true };

        Assert.True(progress.AdvanceTime(0.25d));
    }
}
