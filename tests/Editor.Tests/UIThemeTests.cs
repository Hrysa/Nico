using Engine.Graphics;
using Engine.UI;
using System.Numerics;
using Xunit;

namespace Editor.Tests;

public class UIThemeTests
{
    /// <summary>Verifies the accessibility theme meets enhanced text contrast on its main surfaces.</summary>
    [Fact]
    public void HighContrastTheme_PrimaryTextAndAccentMeetEnhancedContrast()
    {
        var theme = UITheme.HighContrast;

        Assert.True(ContrastRatio(theme.TextPrimary, theme.Surface) >= 7f);
        Assert.True(ContrastRatio(theme.Accent, theme.Surface) >= 7f);
        Assert.NotEqual(theme.Surface, theme.BorderStrong);
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

    /// <summary>Calculates WCAG contrast from the engine's linear color components.</summary>
    /// <param name="first">First linear RGB color.</param>
    /// <param name="second">Second linear RGB color.</param>
    /// <returns>Contrast ratio from one through twenty-one.</returns>
    private static float ContrastRatio(Color first, Color second)
    {
        var firstLuminance = first.R * 0.2126f + first.G * 0.7152f + first.B * 0.0722f;
        var secondLuminance = second.R * 0.2126f + second.G * 0.7152f + second.B * 0.0722f;
        var lighter = MathF.Max(firstLuminance, secondLuminance);
        var darker = MathF.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05f) / (darker + 0.05f);
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
            command => command.Type == UIDrawCommandType.RoundedRectangle);
    }

    /// <summary>Verifies static box styling suppresses state paint without changing toggle state.</summary>
    [Fact]
    public void ToggleButton_StaticVisualState_PreservesBehaviorWithoutStateFill()
    {
        var button = new ToggleButton(100f, 30f, "Header", UITheme.Dark)
        {
            VisualStateMode = BoxVisualStateMode.Static,
            IsChecked = true
        };

        button.SetHover(true);
        button.SetPressed(true);
        var commands = button.BuildDrawList().Commands;

        Assert.True(button.IsChecked);
        Assert.True(button.IsHovered);
        Assert.True(button.IsPressed);
        Assert.DoesNotContain(commands,
            command => command.Type is UIDrawCommandType.Rectangle or
                UIDrawCommandType.RoundedRectangle);
    }

    /// <summary>Verifies convenience labels size buttons while explicit width is preserved.</summary>
    [Fact]
    public void Button_AutoWidth_FollowsContentWhileExplicitWidthIsPreserved()
    {
        var shortButton = new Button(28f, "Go", UITheme.Dark);
        var longButton = new Button(28f, "Open Project", UITheme.Dark);
        var explicitButton = new Button(123f, 28f, "Go", UITheme.Dark);
        var available = new Vector2(float.PositiveInfinity, 28f);
        shortButton.Measure(available);
        longButton.Measure(available);

        Assert.True(longButton.DesiredSize.X > shortButton.DesiredSize.X);
        Assert.Equal(123f, explicitButton.Width);

        Assert.IsType<Label>(explicitButton.Content).Text = "A much longer action";
        explicitButton.Measure(available);
        Assert.Equal(123f, explicitButton.Width);
    }

    /// <summary>Verifies content-sized buttons remeasure when their label grows beyond two characters.</summary>
    [Fact]
    public void Button_AutoWidth_ExpandsWhenLabelTextChanges()
    {
        var button = new Button(28f, "Go", UITheme.Dark);
        var label = Assert.IsType<Label>(button.Content);
        var available = new Vector2(float.PositiveInfinity, 28f);
        button.Measure(available);
        var initialWidth = button.DesiredSize.X;

        label.Text = "Continue";
        button.Measure(available);

        Assert.True(button.DesiredSize.X > initialWidth);
    }

    /// <summary>Verifies a button can host an arbitrary icon-and-label layout.</summary>
    [Fact]
    public void Button_ContentConstructor_AllowsCustomLayout()
    {
        var icon = new Box(12f, 12f) { BackgroundColor = Color.White };
        var label = new Label("Import") { PaddingLeft = 0f };
        var content = UI.Row(UITheme.Dark.Surface, icon, label.Grow());
        content.Gap = 4f;
        content.AlignItems = FlexAlignment.Center;
        var button = new Button(100f, 30f, UITheme.Dark) { Content = content };

        button.Measure(new Vector2(100f, 30f));
        button.Arrange(Vector2.Zero, new Vector2(100f, 30f));

        Assert.Same(content, button.Content);
        Assert.Contains(button.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text && command.Text == "Import");
    }

    /// <summary>Verifies fixed button content cannot paint across neighboring controls.</summary>
    [Fact]
    public void Button_Content_IsClippedToButtonBounds()
    {
        var button = new Button(40f, 24f, "A label wider than its button", UITheme.Dark);

        var text = Assert.Single(button.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text);

        Assert.True(button.ClipToBounds);
        Assert.Equal(new UIClipRect(button.Left, button.Top, button.Right, button.Bottom), text.Clip);
    }

    /// <summary>Verifies a surface paints one fill and four inset border edges.</summary>
    [Fact]
    public void Surface_WithBorder_EmitsFiveRectangles()
    {
        var surface = new Surface(UITheme.Dark.Surface, UITheme.Dark.Border, 100f, 50f);

        var commands = surface.BuildDrawList().Commands;

        Assert.Equal(1, commands.Count(command => command.Type == UIDrawCommandType.RoundedRectangle));
        Assert.Equal(4, commands.Count(command => command.Type == UIDrawCommandType.Rectangle));
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
        Assert.Empty(commands.Where(command => command.Type == UIDrawCommandType.RoundedRectangle));
    }

    /// <summary>Verifies panel corners are rounded with theme radius by default.</summary>
    [Fact]
    public void Panel_DefaultRadius_FromTheme()
    {
        var panel = new Panel(UITheme.Dark.Canvas, 100f, 50f);

        var command = Assert.Single(panel.BuildDrawList().Commands);

        Assert.Equal(UIDrawCommandType.RoundedRectangle, command.Type);
        Assert.Equal(UITheme.Dark.PanelCornerRadius, command.CornerRadius);
    }

    /// <summary>Verifies partial corner modes square only the opposite horizontal edge.</summary>
    [Fact]
    public void Box_PartialCornerModes_PaintTopAndBottomShapes()
    {
        var top = new Panel(Color.Red, 100f, 30f)
        {
            CornerRadius = 6f,
            CornerMode = BoxCornerMode.Top
        };
        var bottom = new Panel(Color.Red, 100f, 30f)
        {
            CornerRadius = 6f,
            CornerMode = BoxCornerMode.Bottom
        };

        var topCommands = top.BuildDrawList().Commands;
        var bottomCommands = bottom.BuildDrawList().Commands;

        Assert.Contains(topCommands, command =>
            command.Type == UIDrawCommandType.RoundedRectangle && command.CornerRadius == 6f);
        Assert.Contains(topCommands, command =>
            command.Type == UIDrawCommandType.Rectangle &&
            command.Left == 0f && command.Top == 24f &&
            command.Right == 6f && command.Bottom == 30f);
        Assert.Contains(topCommands, command =>
            command.Type == UIDrawCommandType.Rectangle &&
            command.Left == 94f && command.Top == 24f &&
            command.Right == 100f && command.Bottom == 30f);
        Assert.Contains(bottomCommands, command =>
            command.Type == UIDrawCommandType.RoundedRectangle && command.CornerRadius == 6f);
        Assert.Contains(bottomCommands, command =>
            command.Type == UIDrawCommandType.Rectangle &&
            command.Left == 0f && command.Top == 0f &&
            command.Right == 6f && command.Bottom == 6f);
        Assert.Contains(bottomCommands, command =>
            command.Type == UIDrawCommandType.Rectangle &&
            command.Left == 94f && command.Top == 0f &&
            command.Right == 100f && command.Bottom == 6f);
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

    /// <summary>Verifies dock tabs supply the persistent Editor panel chrome.</summary>
    [Fact]
    public void EditorView_DockTabs_UseOneHeaderStandard()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var descendants = Descendants(view.Root).ToArray();

        Assert.Contains(descendants, element => element is TabControl);
        Assert.DoesNotContain(descendants, element => element.Name == "GameHeader");
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

    /// <summary>Verifies scrolling rebinds retained list containers instead of replacing them.</summary>
    [Fact]
    public void ListView_Scroll_ReusesVisibleContainers()
    {
        var list = new ListView(200f, 100f);
        list.SetItems(Enumerable.Range(0, 100).Select(index => $"Item {index}"));
        list.BuildDrawList();
        var firstRow = Assert.IsType<ListViewItem>(list.Children[0]);

        list.InvokeScroll(-1f);

        Assert.Same(firstRow, list.Children[0]);
        Assert.Equal("Item 3", firstRow.Text);
    }

    /// <summary>Verifies very large logical lists retain only viewport-sized visual rows.</summary>
    [Fact]
    public void ListView_LargeItems_BoundsVisualChildren()
    {
        var list = new ListView(200f, 100f);
        list.SetItems(Enumerable.Range(0, 100_000).Select(index => index.ToString()));
        list.BuildDrawList();

        Assert.True(list.Children.Count <= (int)MathF.Ceiling(list.Height / list.RowHeight));
    }

    /// <summary>Verifies no-op scrolling is allocation-free and text rebinding remains tightly bounded.</summary>
    [Fact]
    public void ListView_RepeatedScroll_BoundsRebindingAllocation()
    {
        var items = Enumerable.Range(0, 100).Select(index => $"Item {index}").ToArray();
        var list = new ListView(200f, 100f);
        list.SetItems(items);
        list.BuildDrawList();
        list.InvokeScroll(-1f);
        list.InvokeScroll(1f);
        var noOpStart = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++)
            list.InvokeScroll(0f);
        var noOpAllocation = GC.GetAllocatedBytesForCurrentThread() - noOpStart;
        Assert.Equal(0, noOpAllocation);
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
            list.InvokeScroll((index & 1) == 0 ? -1f : 1f);

        var rebindAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Assert.InRange(rebindAllocation, 0, 512 * 100);
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

    /// <summary>Verifies a focused long value keeps its editable tail and caret visible.</summary>
    [Fact]
    public void TextField_FocusedLongValue_ShowsEditableTail()
    {
        var field = new TextField(180f, 30f)
        {
            Text = "ExampleGame.MoveMainObject, example_game.Scripts"
        };
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);

        router.TextInput('X');

        var textCommand = Assert.Single(field.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text);
        Assert.EndsWith("ScriptsX", textCommand.Text);
        Assert.Equal(textCommand.Text.Length, textCommand.CaretIndex);
        Assert.EndsWith("ScriptsX", field.Text);
    }

    /// <summary>Verifies focused text uses proportional glyph widths instead of a two-character estimate.</summary>
    [Fact]
    public void TextField_FocusedNarrowValue_UsesFullEditingWidth()
    {
        var field = new TextField(60f, 30f) { Text = "123456" };
        var router = new UIEventRouter(field, () => { });
        router.Focus(field);

        var textCommand = Assert.Single(field.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text);

        Assert.True(textCommand.Text.Length >= 5);
        Assert.Equal(textCommand.Text.Length, textCommand.CaretIndex);
        Assert.Equal(field.Left + 4f, textCommand.Left);
    }

    /// <summary>Verifies multiline text produces one retained text command per visible logical line.</summary>
    [Fact]
    public void TextBox_MultilinePaint_UsesSeparateLinesAndCaret()
    {
        var textBox = new TextBox(180f, 80f) { Text = "first\nsecond" };
        var router = new UIEventRouter(textBox, () => { });
        router.Focus(textBox);

        var textCommands = textBox.BuildDrawList().Commands
            .Where(command => command.Type == UIDrawCommandType.Text)
            .ToArray();

        Assert.Equal(2, textCommands.Length);
        Assert.Equal("first", textCommands[0].Text);
        Assert.Equal("second", textCommands[1].Text);
        Assert.Equal(6, textCommands[1].CaretIndex);
        Assert.True(textCommands[1].Top > textCommands[0].Top);
    }

    /// <summary>Verifies password fields mask retained draw text without changing the stored buffer.</summary>
    [Fact]
    public void PasswordField_RevealPolicy_ChangesDisplayOnly()
    {
        var field = new PasswordField(180f, 30f) { Text = "secret" };

        var masked = Assert.Single(field.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text);
        Assert.Equal("••••••", masked.Text);
        Assert.Equal("secret", field.Text);

        field.PasswordRevealMode = PasswordRevealMode.Always;
        var revealed = Assert.Single(field.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text);
        Assert.Equal("secret", revealed.Text);
    }

    /// <summary>Verifies text validation updates semantic state, events, and border styling.</summary>
    [Fact]
    public void TextField_Validator_UpdatesErrorStateAndVisual()
    {
        var field = new TextField(180f, 30f);
        var changes = new List<string?>();
        field.ValidationChanged += changes.Add;
        field.Validator = text => text.Length < 3 ? "Too short" : null;

        Assert.True(field.HasValidationError);
        Assert.Equal("Too short", field.ValidationMessage);
        field.BuildDrawList();
        Assert.Equal(UITheme.Dark.Error, field.BorderColor);

        field.Text = "valid";
        field.BuildDrawList();
        Assert.False(field.HasValidationError);
        Assert.Equal(UITheme.Dark.BorderStrong, field.BorderColor);
        Assert.Equal(new string?[] { "Too short", null }, changes);
    }

    /// <summary>Verifies text semantics expose validation while protecting password values.</summary>
    [Fact]
    public void TextEditors_Semantics_ExposeStateAndProtectPasswords()
    {
        var field = new TextField(180f, 30f)
        {
            Name = "Username",
            Text = "ab",
            Validator = text => text.Length < 3 ? "Too short" : null
        };
        var fieldInfo = field.GetSemanticInfo();

        Assert.Equal(UISemanticRole.TextField, fieldInfo.Role);
        Assert.Equal("Username", fieldInfo.Name);
        Assert.Equal("ab", fieldInfo.Value);
        Assert.True(fieldInfo.IsInvalid);
        Assert.Equal("Too short", fieldInfo.ValidationMessage);

        var password = new PasswordField(180f, 30f) { Text = "secret" };
        var passwordInfo = password.GetSemanticInfo();
        Assert.Equal(UISemanticRole.PasswordField, passwordInfo.Role);
        Assert.Null(passwordInfo.Value);
    }

    /// <summary>Verifies the editor shell prioritizes Scene while retaining hierarchy, files, Game, and Inspector docks.</summary>
    [Fact]
    public void EditorView_ReferenceLayout_HasExpectedDockHierarchy()
    {
        var view = EditorUI.BuildView(1280f, 720f);

        Assert.True(view.SceneViewport.Height > view.GameViewport.Height * 2f);
        var descendants = Descendants(view.Root).ToArray();
        Assert.Contains(view.FileSystemTree, descendants);
        Assert.Contains(view.Inspector, descendants);
        Assert.Contains(descendants, child => child.Name == "BottomDock");
        Assert.Contains(descendants, child => child.Name == "SceneToolbar");
        Assert.Equal(TitleBar.DefaultHeight, view.TitleBar.Height);
        Assert.Equal(Thickness.Zero, view.TitleBar.Margin);
        Assert.Equal(0f, view.TitleBar.BorderThickness);
        Assert.Same(view.PlayButtonIcon, view.PlayButton.Content);
        Assert.Equal(IconKind.Play, view.PlayButtonIcon.Kind);
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
