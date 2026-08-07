using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class SelectionControlTests
{
    /// <summary>Verifies checkbox clicks toggle persistent state and notify once.</summary>
    [Fact]
    public void CheckBox_Click_TogglesCheckedState()
    {
        var checkBox = new CheckBox(100f, 30f, "Enabled");
        var changes = 0;
        checkBox.CheckedChanged += _ => changes++;
        var router = new UIEventRouter(checkBox, () => { });
        router.MovePointer(new Vector2(20f, 15f));

        router.Press();
        router.Release(true);
        Assert.True(checkBox.IsChecked);
        router.Press();
        router.Release(true);

        Assert.False(checkBox.IsChecked);
        Assert.Equal(2, changes);
    }

    /// <summary>Verifies radio exclusivity is limited to same-parent, same-name groups.</summary>
    [Fact]
    public void RadioButton_Click_UnchecksSiblingInSameGroup()
    {
        var root = new Panel(Color.Black, 200f, 50f);
        var first = new RadioButton(100f, 30f, "First") { GroupName = "Mode", IsChecked = true };
        var second = new RadioButton(100f, 30f, "Second") { GroupName = "Mode" };
        root.AddChild(first);
        root.AddChild(second);

        second.InvokeClick();

        Assert.False(first.IsChecked);
        Assert.True(second.IsChecked);
        second.InvokeClick();
        Assert.True(second.IsChecked);
    }

    /// <summary>Verifies toggle switches paint checked tracks and knobs semantically.</summary>
    [Fact]
    public void ToggleSwitch_Checked_PaintsAccentTrackAndKnob()
    {
        var toggle = new ToggleSwitch { IsChecked = true };

        var commands = toggle.BuildDrawList().Commands;

        Assert.Contains(commands, command => command.Color == UITheme.Dark.AccentPressed);
        Assert.Contains(commands, command => command.Type == UIDrawCommandType.Ellipse &&
            command.Color == UITheme.Dark.Accent);
    }

    /// <summary>Verifies numeric text and keyboard steps share one clamped value.</summary>
    [Fact]
    public void NumericField_TextAndKeys_UpdateClampedValue()
    {
        var field = new NumericField(140f, 30f)
        {
            Minimum = 0d,
            Maximum = 10d,
            Value = 5d,
            Step = 2d
        };
        field.BuildDrawList();
        var router = new UIEventRouter(field, () => { });
        router.Focus(field.TextField);
        router.RouteKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));
        router.RouteText("7.5");
        Assert.Equal(5d, field.Value);
        router.RouteKey(new KeyInputEvent(InputKey.Enter, true, false, InputModifiers.None));
        Assert.Equal(7.5d, field.Value);

        router.RouteKey(new KeyInputEvent(InputKey.Up, true, false, InputModifiers.None));
        Assert.Equal(9.5d, field.Value);
        router.RouteKey(new KeyInputEvent(InputKey.Up, true, false, InputModifiers.None));

        Assert.Equal(10d, field.Value);
        Assert.Equal("10", field.TextField.Text);
    }
}
