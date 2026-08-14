using System.Numerics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class ColorPickerTests
{
    /// <summary>Round-trips display-referred hexadecimal text through linear color storage.</summary>
    [Fact]
    public void HexValue_RoundTripsSrgbAndAlpha()
    {
        Assert.True(ColorPicker.TryParseHex("#FF800040", allowAlpha: true, out var color));

        Assert.Equal(1f, color.X);
        Assert.InRange(color.Y, 0.2158f, 0.2160f);
        Assert.Equal(0f, color.Z);
        Assert.InRange(color.W, 0.2509f, 0.2511f);
        Assert.Equal("#FF800040", ColorPicker.FormatHex(color, includeAlpha: true));
    }

    /// <summary>Accepts compact forms and rejects alpha when the picker is RGB-only.</summary>
    [Fact]
    public void TryParseHex_HandlesCompactAndRgbOnlyForms()
    {
        Assert.True(ColorPicker.TryParseHex("#0F8", allowAlpha: false, out var color));
        Assert.Equal("#00FF88", ColorPicker.FormatHex(color, includeAlpha: false));
        Assert.False(ColorPicker.TryParseHex("#0F8C", allowAlpha: false, out _));
        Assert.False(ColorPicker.TryParseHex("#00FF88CC", allowAlpha: false, out _));
    }

    /// <summary>Raises changes for edits while silent synchronization remains non-destructive.</summary>
    [Fact]
    public void Value_ReportsEditsAndSilentRefreshDoesNotNotify()
    {
        var picker = new ColorPicker(180f, 30f, showAlpha: true);
        var changes = 0;
        picker.ValueChanged += _ => changes++;

        Assert.True(picker.TrySetHex("#33669980"));
        Assert.Equal(1, changes);
        picker.SetValueWithoutNotification(Vector4.One);

        Assert.Equal(1, changes);
        Assert.Equal(Vector4.One, picker.Value);
    }

    /// <summary>Exposes an accessible expand action that toggles its owned popup.</summary>
    [Fact]
    public void SemanticExpandCollapse_TogglesPopup()
    {
        var picker = new ColorPicker(180f, 30f);

        Assert.True(picker.PerformSemanticAction(UISemanticAction.ExpandCollapse));
        Assert.True(picker.IsDropDownOpen);
        Assert.True(picker.GetSemanticInfo().IsExpanded);
        Assert.True(picker.PerformSemanticAction(UISemanticAction.ExpandCollapse));
        Assert.False(picker.IsDropDownOpen);
    }
}
