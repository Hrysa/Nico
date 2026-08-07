using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies reusable symbolic and texture-backed icons.</summary>
public sealed class IconTests
{
    /// <summary>Verifies check icons emit two scaled semantic strokes.</summary>
    [Fact]
    public void Check_PaintsScaledLineGeometry()
    {
        var icon = new Icon(IconKind.Check, 20f)
        {
            ForegroundColor = Color.Green,
            StrokeThickness = 2f
        };

        var commands = icon.BuildDrawList().Commands;

        Assert.Equal(2, commands.Count);
        Assert.All(commands, command =>
        {
            Assert.Equal(UIDrawCommandType.Line, command.Type);
            Assert.Equal(Color.Green, command.Color);
            Assert.Equal(2f, command.StrokeWidth);
        });
        Assert.Equal(3.2f, commands[0].Left, 3);
        Assert.Equal(10.4f, commands[0].Top, 3);
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
            Padding = 8f,
            Content = new Icon(IconKind.Close, 16f)
        };

        var commands = button.BuildDrawList().Commands;

        Assert.Equal(2, commands.Count);
        Assert.All(commands, command => Assert.Equal(UIDrawCommandType.Line, command.Type));
    }

    /// <summary>Verifies search icons use a true ellipse stroke instead of polygon segments.</summary>
    [Fact]
    public void Search_PaintsAnalyticLensAndHandle()
    {
        var icon = new Icon(IconKind.Search, 20f)
        {
            ForegroundColor = Color.White,
            StrokeThickness = 2f
        };

        var commands = icon.BuildDrawList().Commands;

        Assert.Equal(2, commands.Count);
        Assert.Equal(UIDrawCommandType.StrokedEllipse, commands[0].Type);
        Assert.Equal(2f, commands[0].StrokeWidth);
        Assert.Equal(UIDrawCommandType.Line, commands[1].Type);
    }
}
