using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIThemeTests
{
    /// <summary>Verifies the editor theme retains the sampled reference palette.</summary>
    [Fact]
    public void DarkTheme_UsesReferencePalette()
    {
        var baseColor = Color.FromSrgb(0x12, 0x13, 0x14);
        Assert.Equal(baseColor.R, UITheme.Dark.Canvas.R, 6);
        Assert.Equal(baseColor.G, UITheme.Dark.Canvas.G, 6);
        Assert.Equal(baseColor.B, UITheme.Dark.Canvas.B, 6);
        Assert.Equal(UITheme.Dark.Canvas.R, UITheme.Dark.Surface.R, 4);
        Assert.Equal(Color.FromSrgb(0x1C, 0x1C, 0x1C).R, UITheme.Dark.Field.R, 6);
        Assert.Equal(Color.FromSrgb(0x23, 0x24, 0x25).R, UITheme.Dark.SurfaceHover.R, 6);
        Assert.Equal(Color.FromSrgb(0x2C, 0x2D, 0x2E).R, UITheme.Dark.SurfacePressed.R, 6);
        Assert.Equal(Color.FromSrgb(0xC9, 0xC9, 0xC9).R, UITheme.Dark.TextPrimary.R, 6);
        Assert.Equal(Color.FromSrgb(0x68, 0x9C, 0xF8).R, UITheme.Dark.Accent.R, 6);
        Assert.Equal(Color.FromSrgb(0x68, 0x9C, 0xF8).G, UITheme.Dark.Accent.G, 6);
        Assert.Equal(Color.FromSrgb(0x68, 0x9C, 0xF8).B, UITheme.Dark.Accent.B, 6);
        Assert.Equal(17.5f, UITheme.Dark.FontSize);
        Assert.Equal(15f, UITheme.Dark.CaptionFontSize);
        Assert.Equal(18f, UITheme.Dark.PanelTitleFontSize);
    }

    /// <summary>Verifies idle subtle buttons emit readable text without a background rectangle.</summary>
    [Fact]
    public void Button_ThemedSubtle_EmitsOnlyTextWhenIdle()
    {
        var button = new Button(0f, 0f, 100f, 30f, "Open", UITheme.Dark);

        var commands = button.BuildDrawList().Commands;

        Assert.DoesNotContain(commands, command => command.Type == UIDrawCommandType.Rectangle);
        Assert.Contains(commands, command => command.Type == UIDrawCommandType.Text && command.Text == "Open");
    }

    /// <summary>Verifies subtle buttons paint a surface while hovered.</summary>
    [Fact]
    public void Button_ThemedSubtle_EmitsBackgroundWhenHovered()
    {
        var button = new Button(0f, 0f, 100f, 30f, "Open", UITheme.Dark);

        button.SetHover(true);

        Assert.Contains(button.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Rectangle);
    }

    /// <summary>Verifies a surface paints one fill and four inset border edges.</summary>
    [Fact]
    public void Surface_WithBorder_EmitsFiveRectangles()
    {
        var surface = new Surface(0f, 0f, 100f, 50f, UITheme.Dark.Surface, UITheme.Dark.Border);

        var commands = surface.BuildDrawList().Commands;

        Assert.Equal(5, commands.Count(command => command.Type == UIDrawCommandType.Rectangle));
    }

    /// <summary>Verifies transparent dock surfaces omit redundant full-panel fill geometry.</summary>
    [Fact]
    public void Surface_WithoutBackground_EmitsOnlyBorderRectangles()
    {
        var surface = new Surface(0f, 0f, 100f, 50f, UITheme.Dark.Surface, UITheme.Dark.Border)
        {
            PaintBackground = false
        };

        var commands = surface.BuildDrawList().Commands;

        Assert.Equal(4, commands.Count(command => command.Type == UIDrawCommandType.Rectangle));
    }

    /// <summary>Verifies section headers use the same fill as their panel content.</summary>
    [Fact]
    public void SectionHeader_UsesPanelSurfaceColor()
    {
        var header = new SectionHeader(0f, 0f, 200f, 36f, "Inspector", UITheme.Dark);

        Assert.Equal(UITheme.Dark.Surface.R, header.BackgroundColor.R, 4);
        Assert.Equal(UITheme.Dark.Surface.G, header.BackgroundColor.G, 4);
        Assert.Equal(UITheme.Dark.Surface.B, header.BackgroundColor.B, 4);
    }

    /// <summary>Verifies floating component subtrees are assigned to the overlay composition layer.</summary>
    [Fact]
    public void ContextMenu_DrawList_UsesOverlayLayerForSurfaceAndText()
    {
        var root = new Panel(0f, 0f, 300f, 300f, UITheme.Dark.Canvas);
        var menu = new ContextMenu(20f, 20f, 160f);
        menu.AddItem("Open", () => { });
        root.AddChild(menu);

        var commands = root.BuildDrawList().Commands;

        Assert.Contains(commands, command => command.Layer == UIDrawLayer.Content);
        Assert.All(commands.Where(command => command.Text == "Open"),
            command => Assert.Equal(UIDrawLayer.Overlay, command.Layer));
        Assert.True(commands.Count(command => command.Layer == UIDrawLayer.Overlay) >= 2);
    }

    /// <summary>Verifies a list row click updates selection and reports its item.</summary>
    [Fact]
    public void ListView_ClickRow_UpdatesSelection()
    {
        var list = new ListView(0f, 0f, 200f, 100f);
        list.SetItems(["First", "Second"]);
        var selected = string.Empty;
        list.SelectionChanged += (_, item) => selected = item;
        var router = new UIEventRouter(list, () => { });
        router.MovePointer(new(20f, 45f));
        router.Press();

        router.Release(invokeClick: true);

        Assert.Equal(1, list.SelectedIndex);
        Assert.Equal("Second", selected);
    }

    /// <summary>Verifies focused text fields receive characters and editing keys through the UI router.</summary>
    [Fact]
    public void TextField_Focused_HandlesTextInputAndBackspace()
    {
        var field = new TextField(0f, 0f, 200f, 30f) { Placeholder = "Filename" };
        var router = new UIEventRouter(field, () => { });
        router.MovePointer(new(20f, 15f));
        router.Press();
        router.Release(invokeClick: true);

        router.TextInput('A');
        router.TextInput('B');
        router.KeyDown((int)InputKey.Backspace);

        Assert.True(field.IsFocused);
        Assert.Equal("A", field.Text);
        Assert.Contains(field.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text && command.Text == "A|");
    }

    /// <summary>Verifies the editor shell prioritizes Scene while retaining hierarchy, files, Game, and Inspector docks.</summary>
    [Fact]
    public void EditorView_ReferenceLayout_HasExpectedDockHierarchy()
    {
        var view = EditorUI.BuildView(1280f, 720f);

        Assert.True(view.SceneViewport.Height > view.GameViewport.Height * 2f);
        Assert.Contains(view.Root.Children, child => child.Name == "FileSystem");
        Assert.Contains(view.Root.Children, child => child.Name == "Inspector");
        Assert.Contains(view.Root.Children, child => child.Name == "BottomDock");
        Assert.Contains(view.Root.Children, child => child.Name == "SceneToolbar");
    }

    /// <summary>Verifies title-bar drag regions and window buttons dispatch separate actions.</summary>
    [Fact]
    public void TitleBar_DragRegionAndMinimizeButton_DispatchExpectedActions()
    {
        var titleBar = new TitleBar(800f, 30f, "Project", style: TitleBarStyle.Windows);
        var dragged = false;
        var minimized = false;
        titleBar.DragStarted += () => dragged = true;
        titleBar.MinimizeRequested += () => minimized = true;
        var router = new UIEventRouter(titleBar, () => { });

        router.MovePointer(new(100f, 15f));
        router.Press();
        router.Release(invokeClick: true);
        Assert.True(dragged);

        dragged = false;
        router.MovePointer(new(710f, 15f));
        router.Press();
        router.Release(invokeClick: true);

        Assert.False(dragged);
        Assert.True(minimized);
    }

    /// <summary>Verifies macOS title bars use traffic-light ellipses and left-side controls.</summary>
    [Fact]
    public void TitleBar_MacOS_UsesLeftTrafficLightControls()
    {
        var titleBar = new TitleBar(800f, 30f, "Project", style: TitleBarStyle.MacOS);
        var closed = false;
        titleBar.CloseRequested += () => closed = true;
        var router = new UIEventRouter(titleBar, () => { });

        Assert.Equal(3, titleBar.BuildDrawList().Commands.Count(
            command => command.Type == UIDrawCommandType.Ellipse));
        router.MovePointer(new(20f, 15f));
        router.Press();
        router.Release(invokeClick: true);

        Assert.True(closed);
    }
}
