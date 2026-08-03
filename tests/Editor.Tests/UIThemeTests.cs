using Engine.Graphics;
using Engine.UI;
using System.Numerics;
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
        Assert.Equal(36f, UITheme.Dark.PanelHeaderHeight);
        Assert.Equal(10f, UITheme.Dark.PanelHeaderPadding);
        Assert.Equal(30f, UITheme.Dark.ItemRowHeight);
        Assert.Equal(10f, UITheme.Dark.ItemRowPadding);
    }

    /// <summary>Verifies idle subtle buttons emit readable text without a background rectangle.</summary>
    [Fact]
    public void Button_ThemedSubtle_EmitsOnlyTextWhenIdle()
    {
        var button = new Button(100f, 30f, "Open", UITheme.Dark);

        var commands = button.BuildDrawList().Commands;

        Assert.DoesNotContain(commands, command => command.Type == UIDrawCommandType.Rectangle);
        Assert.Contains(commands, command => command.Type == UIDrawCommandType.Text && command.Text == "Open");
    }

    /// <summary>Verifies buttons compose and arrange a label instead of painting text themselves.</summary>
    [Fact]
    public void Button_TextConstructor_ComposesLabelInsidePaddingBox()
    {
        var button = new Button(100f, 30f, "Open", UITheme.Dark)
        {
            Padding = new Thickness(8f, 2f, 6f, 3f)
        };

        button.Measure(new Vector2(100f, 30f));
        button.Arrange(new Vector2(10f, 20f), new Vector2(100f, 30f));

        var label = Assert.IsType<Label>(button.Content);
        Assert.Same(label, Assert.Single(button.Children));
        Assert.False(label.IsHitTestVisible);
        Assert.Equal(8f, label.Position.X);
        Assert.Equal(2f, label.Position.Y);
        Assert.Equal(86f, label.Width);
        Assert.Equal(25f, label.Height);
    }

    /// <summary>Verifies a scalar thickness expands to all four box-model sides.</summary>
    [Fact]
    public void Thickness_UniformValue_ProvidesCombinedInsets()
    {
        Thickness thickness = 4f;

        Assert.Equal(4f, thickness.Left);
        Assert.Equal(4f, thickness.Top);
        Assert.Equal(8f, thickness.Horizontal);
        Assert.Equal(8f, thickness.Vertical);
    }

    /// <summary>Verifies subtle buttons paint a surface while hovered.</summary>
    [Fact]
    public void Button_ThemedSubtle_EmitsBackgroundWhenHovered()
    {
        var button = new Button(100f, 30f, "Open", UITheme.Dark);

        button.SetHover(true);

        Assert.Contains(button.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Rectangle);
    }

    /// <summary>Verifies convenience labels size buttons while explicit width is preserved.</summary>
    [Fact]
    public void Button_AutoWidth_FollowsContentWhileExplicitWidthIsPreserved()
    {
        var shortButton = new Button(28f, "Go", UITheme.Dark);
        var longButton = new Button(28f, "Open Project", UITheme.Dark);
        var explicitButton = new Button(123f, 28f, "Go", UITheme.Dark);

        Assert.True(longButton.Width > shortButton.Width);
        Assert.Equal(123f, explicitButton.Width);

        Assert.IsType<Label>(explicitButton.Content).Text = "A much longer action";
        explicitButton.Measure(new Vector2(float.PositiveInfinity, 28f));
        Assert.Equal(123f, explicitButton.Width);
    }

    /// <summary>Verifies a button can host an arbitrary icon-and-label layout.</summary>
    [Fact]
    public void Button_ContentConstructor_AllowsCustomLayout()
    {
        var content = new Grid(UITheme.Dark.Surface);
        content.Rows.Add(GridLength.Star());
        content.Columns.Add(GridLength.Pixels(12f));
        content.Columns.Add(GridLength.Pixels(4f));
        content.Columns.Add(GridLength.Star());
        var icon = new Box(12f, 12f) { BackgroundColor = Color.White };
        var label = new Label("Import") { PaddingLeft = 0f };
        content.Add(icon, 0, 0);
        content.Add(label, 0, 2);
        var button = new Button(100f, 30f, UITheme.Dark) { Content = content };

        button.Measure(new Vector2(100f, 30f));
        button.Arrange(Vector2.Zero, new Vector2(100f, 30f));

        Assert.Same(content, button.Content);
        Assert.Contains(button.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text && command.Text == "Import");
    }

    /// <summary>Verifies a surface paints one fill and four inset border edges.</summary>
    [Fact]
    public void Surface_WithBorder_EmitsFiveRectangles()
    {
        var surface = new Surface(UITheme.Dark.Surface, UITheme.Dark.Border, 100f, 50f);

        var commands = surface.BuildDrawList().Commands;

        Assert.Equal(5, commands.Count(command => command.Type == UIDrawCommandType.Rectangle));
    }

    /// <summary>Verifies transparent dock surfaces omit redundant full-panel fill geometry.</summary>
    [Fact]
    public void Surface_WithoutBackground_EmitsOnlyBorderRectangles()
    {
        var surface = new Surface(UITheme.Dark.Surface, UITheme.Dark.Border, 100f, 50f)
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
        var header = new SectionHeader(200f, "Inspector", UITheme.Dark);

        Assert.Equal(UITheme.Dark.Surface.R, header.BackgroundColor.R, 4);
        Assert.Equal(UITheme.Dark.Surface.G, header.BackgroundColor.G, 4);
        Assert.Equal(UITheme.Dark.Surface.B, header.BackgroundColor.B, 4);
        Assert.Equal(UITheme.Dark.PanelHeaderHeight, header.Height);
        Assert.Equal(UITheme.Dark.PanelTitleFontSize, header.TitleLabel.FontSize);
    }

    /// <summary>Verifies every persistent editor dock uses the shared panel composition and tokens.</summary>
    [Fact]
    public void EditorView_ToolPanels_UseOneHeaderStandard()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var panels = Descendants(view.Root).OfType<ToolPanel>().ToArray();

        Assert.Equal(3, panels.Length);
        Assert.All(panels, panel =>
        {
            Assert.Equal(UITheme.Dark.PanelHeaderHeight, panel.Header.Height);
            Assert.Equal(UITheme.Dark.PanelTitleFontSize, panel.Header.TitleLabel.FontSize);
            Assert.Equal(UITheme.Dark.PanelHeaderPadding, panel.Header.Padding.Left);
            Assert.Equal(UITheme.Dark.PanelHeaderPadding, panel.Header.Padding.Right);
            Assert.Equal(panel.Header.Height, panel.Content.Position.Y);
            Assert.Equal(panel.Width, panel.Content.Width);
        });
    }

    /// <summary>Verifies floating component subtrees are assigned to the overlay composition layer.</summary>
    [Fact]
    public void ContextMenu_DrawList_UsesOverlayLayerForSurfaceAndText()
    {
        var root = new Panel(UITheme.Dark.Canvas, 300f, 300f);
        var menu = new ContextMenu(160f);
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
        var list = new ListView(200f, 100f);
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

    /// <summary>Verifies hierarchy and filesystem rows share size and typography tokens.</summary>
    [Fact]
    public void TreeAndListRows_UseSameVisualMetrics()
    {
        var node = new Engine.Core.Node { Name = "Object" };
        var tree = new TreeView(200f, 100f, UITheme.Dark);
        tree.SetRoots([node]);
        var list = new ListView(200f, 100f, UITheme.Dark);
        list.SetItems(["Object"]);

        var treeRow = Assert.IsType<TreeViewItem>(Assert.Single(tree.Children));
        var listRow = Assert.IsType<ListViewItem>(Assert.Single(list.Children));
        Assert.Equal(list.RowHeight, tree.RowHeight);
        Assert.Equal(listRow.Height, treeRow.Height);
        Assert.Equal(
            Assert.IsType<Label>(listRow.Content).FontSize,
            Assert.IsType<Label>(treeRow.Content).FontSize);
        Assert.Equal(listRow.PaddingLeft, treeRow.PaddingLeft);
    }

    /// <summary>Verifies drag previews paint above content without intercepting drop hit tests.</summary>
    [Fact]
    public void DragPreview_IsNonInteractiveOverlayWithItemLabel()
    {
        var preview = new DragPreview("scene.node");

        var commands = preview.BuildDrawList().Commands;

        Assert.True(preview.IsOverlay);
        Assert.False(preview.IsHitTestVisible);
        Assert.Equal("scene.node", preview.ItemLabel.Text);
        Assert.All(commands, command => Assert.Equal(UIDrawLayer.Overlay, command.Layer));
    }

    /// <summary>Verifies focused text fields receive characters and editing keys through the UI router.</summary>
    [Fact]
    public void TextField_Focused_HandlesTextInputAndBackspace()
    {
        var field = new TextField(200f, 30f) { Placeholder = "Filename" };
        var router = new UIEventRouter(field, () => { });
        router.MovePointer(new(20f, 15f));
        router.Press();
        router.Release(invokeClick: true);

        router.TextInput('A');
        router.TextInput('B');
        router.KeyDown((int)InputKey.Backspace);

        Assert.True(field.IsFocused);
        Assert.Equal("A", field.Text);
        var commands = field.BuildDrawList().Commands;
        Assert.Contains(commands,
            command => command.Type == UIDrawCommandType.Text
                && command.Text == "A" && command.CaretIndex == 1);
        Assert.DoesNotContain(commands,
            command => command.Type == UIDrawCommandType.Text && command.Text.Contains('|'));
    }

    /// <summary>Verifies the editor shell prioritizes Scene while retaining hierarchy, files, Game, and Inspector docks.</summary>
    [Fact]
    public void EditorView_ReferenceLayout_HasExpectedDockHierarchy()
    {
        var view = EditorUI.BuildView(1280f, 720f);

        Assert.True(view.SceneViewport.Height > view.GameViewport.Height * 2f);
        var descendants = Descendants(view.Root).ToArray();
        Assert.Contains(descendants, child => child.Name == "FileSystem");
        Assert.Contains(descendants, child => child.Name == "Inspector");
        Assert.Contains(descendants, child => child.Name == "BottomDock");
        Assert.Contains(descendants, child => child.Name == "SceneToolbar");
        Assert.Equal(1f, view.TitleBar.Margin.Bottom);
        Assert.Equal(640f, view.PlayButton.Left + view.PlayButton.Width / 2f);
        Assert.Equal(view.TitleBar.Top + (view.TitleBar.Height - view.PlayButton.Height) / 2f,
            view.PlayButton.Top);
    }

    /// <summary>Verifies title-bar drag regions and window buttons dispatch separate actions.</summary>
    [Fact]
    public void TitleBar_DragRegionAndMinimizeButton_DispatchExpectedActions()
    {
        var titleBar = new TitleBar(800f, 30f, style: TitleBarStyle.Windows);
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
        var titleBar = new TitleBar(800f, 30f, style: TitleBarStyle.MacOS);
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

    /// <summary>Verifies title-bar content uses semantic left, center, and right zones.</summary>
    [Fact]
    public void TitleBar_Zones_AlignContentWithoutSemanticNameChecks()
    {
        var titleBar = new TitleBar(900f, 30f, style: TitleBarStyle.Windows);
        var left = new Button(60f, 30f, "Left") { Name = "ArbitraryLeft" };
        var center = new Button(60f, 30f, "Center") { Name = "ArbitraryCenter" };
        titleBar.LeftZone.AddChild(left);
        titleBar.CenterZone.AddChild(center);
        titleBar.Measure(new(900f, 30f));
        titleBar.Arrange(new(0f, 0f), new(900f, 30f));

        Assert.Equal(8f, left.Left);
        Assert.Equal(420f, center.Left);
        Assert.Contains(titleBar.RightZone.Children,
            child => child.Name == "WindowClose");
        var close = Assert.IsType<Button>(titleBar.RightZone.Children.Single(
            child => child.Name == "WindowClose"));
        Assert.Equal(titleBar.Right, close.Right);
        Assert.Equal(Color.FromSrgb(0xE8, 0x11, 0x23), close.HoverColor);
        Assert.Equal(Color.FromSrgb(0xC5, 0x0F, 0x1F), close.PressedColor);
        Assert.All(titleBar.RightZone.Children.OfType<Button>(),
            button => Assert.Equal(0f, button.CornerRadius));
        Assert.DoesNotContain(titleBar.LeftZone.Children,
            child => child.Name.StartsWith("Window", StringComparison.Ordinal));
    }

    /// <summary>Verifies macOS window controls occupy the left title-bar zone.</summary>
    [Fact]
    public void TitleBar_MacOS_WindowControlsAreInLeftZone()
    {
        var titleBar = new TitleBar(900f, 30f, style: TitleBarStyle.MacOS);

        Assert.Equal(3, titleBar.LeftZone.Children.Count(
            child => child.Name.StartsWith("Window", StringComparison.Ordinal)));
        Assert.DoesNotContain(titleBar.RightZone.Children,
            child => child.Name.StartsWith("Window", StringComparison.Ordinal));
    }

    /// <summary>Enumerates all descendants beneath one UI element.</summary>
    /// <param name="element">Subtree root.</param>
    /// <returns>Descendants in depth-first order.</returns>
    private static IEnumerable<UIElement> Descendants(UIElement element)
    {
        foreach (var child in element.Children.OfType<UIElement>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
}
