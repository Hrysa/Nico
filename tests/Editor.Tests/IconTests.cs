using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies reusable symbolic and texture-backed icons.</summary>
public sealed class IconTests
{
    /// <summary>Verifies check icons emit the official Codicon font glyph.</summary>
    [Fact]
    public void Check_PaintsCodiconGlyph()
    {
        var icon = new Icon(IconKind.Check, 20f)
        {
            ForegroundColor = Color.Green
        };

        var command = Assert.Single(icon.BuildDrawList().Commands);

        Assert.Equal(UIDrawCommandType.Text, command.Type);
        Assert.Equal(UIFontFamily.Codicon, command.FontFamily);
        Assert.Equal("\uEAB2", command.Text);
        Assert.Equal(Color.Green, command.Color);
        Assert.Equal(20f, command.FontPixelHeight);
    }

    /// <summary>Verifies texture icons use the shared semantic image primitive.</summary>
    [Fact]
    public void Texture_PaintsImageCommand()
    {
        var texture = new TextureHandle(17);
        var icon = new Icon(texture, 24f);

        var command = Assert.Single(icon.BuildDrawList().Commands);

        Assert.Equal(UIDrawCommandType.Image, command.Type);
        Assert.Equal(texture, command.Texture);
        Assert.Equal(24f, command.Right);
        Assert.Equal(24f, command.Bottom);
    }

    /// <summary>Verifies symbolic icons compose as ordinary button content.</summary>
    [Fact]
    public void Symbol_ComposesInsideContentControl()
    {
        var button = new Button(32f, 32f, UITheme.Dark)
        {
            Content = new Icon(IconKind.Close, 16f)
        };

        var commands = button.BuildDrawList().Commands;

        var icon = Assert.Single(
            commands, command => command.Type == UIDrawCommandType.Text);
        Assert.Equal(UIFontFamily.Codicon, icon.FontFamily);
        Assert.Equal("\uEA76", icon.Text);
        Assert.Equal(8f, icon.Left);
        Assert.Equal(8f, icon.Top);
    }

    /// <summary>Verifies search icons use the official Codicon search glyph.</summary>
    [Fact]
    public void Search_PaintsCodiconGlyph()
    {
        var icon = new Icon(IconKind.Search, 20f)
        {
            ForegroundColor = Color.White
        };

        var command = Assert.Single(icon.BuildDrawList().Commands);

        Assert.Equal(UIDrawCommandType.Text, command.Type);
        Assert.Equal(UIFontFamily.Codicon, command.FontFamily);
        Assert.Equal("\uEA6D", command.Text);
    }

    /// <summary>Verifies play and stop states use their official Codicon glyphs.</summary>
    [Fact]
    public void PlayAndStop_PaintCodiconGlyphs()
    {
        var icon = new Icon(IconKind.Play, 20f);

        Assert.Equal("\uEB2C", Assert.Single(icon.BuildDrawList().Commands).Text);

        icon.Kind = IconKind.Stop;

        Assert.Equal("\uEAD7", Assert.Single(icon.BuildDrawList().Commands).Text);
    }
}
