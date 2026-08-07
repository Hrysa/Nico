using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class NavigationControlTests
{
    /// <summary>Verifies outside presses dismiss owned combo popups.</summary>
    [Fact]
    public void ComboBox_OutsidePress_DismissesOwnedPopup()
    {
        var root = new Canvas { Width = 300f, Height = 200f };
        var combo = new ComboBox(120f, 30f);
        combo.SetItems(["One", "Two"]);
        root.Add(combo, Vector2.Zero);
        root.BuildDrawList();
        var router = new UIEventRouter(root, () => { });
        Click(router, 20f, 15f);
        root.BuildDrawList();
        Assert.True(combo.IsDropDownOpen);

        router.Press(new PointerButtonEvent(
            0, new Vector2(250f, 150f), InputPointerButton.Primary, true, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));

        Assert.False(combo.IsDropDownOpen);
    }

    /// <summary>Verifies Escape dismisses the topmost owned popup before normal key routing.</summary>
    [Fact]
    public void ComboBox_Escape_ClosesTopmostPopup()
    {
        var combo = new ComboBox(120f, 30f);
        combo.SetItems(["One"]);
        combo.BuildDrawList();
        var router = new UIEventRouter(combo, () => { });
        Click(router, 20f, 15f);
        Assert.True(combo.IsDropDownOpen);

        router.RouteKey(new KeyInputEvent(InputKey.Escape, true, false, InputModifiers.None));

        Assert.False(combo.IsDropDownOpen);
    }

    /// <summary>Verifies menu-bar headers open owned context menus and invoke rows.</summary>
    [Fact]
    public void MenuBar_HeaderAndItem_UseOwnedPopup()
    {
        var bar = new MenuBar(300f, 30f);
        var menu = new ContextMenu(140f);
        var invoked = false;
        menu.AddItem("Open", () => invoked = true);
        bar.AddMenu("File", menu);
        bar.BuildDrawList();
        var router = new UIEventRouter(bar, () => { });

        Click(router, 20f, 15f);
        bar.BuildDrawList();
        Assert.True(menu.IsOpen);
        Click(router, 20f, 45f);

        Assert.True(invoked);
        Assert.Same(menu.Owner, router.FocusedElement);
    }

    /// <summary>Verifies menu rows wrap with arrows and activate from Enter.</summary>
    [Fact]
    public void ContextMenu_Keyboard_NavigatesAndActivatesRows()
    {
        var menu = new ContextMenu(140f);
        var invoked = false;
        menu.AddItem("First", () => { });
        menu.AddItem("Second", () => invoked = true);
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(menu.Items[0]);

        router.RouteKey(new KeyInputEvent(InputKey.Down, true, false, InputModifiers.None));
        Assert.Same(menu.Items[1], router.FocusedElement);
        router.RouteKey(new KeyInputEvent(InputKey.Enter, true, false, InputModifiers.None));

        Assert.True(invoked);
        Assert.False(menu.IsOpen);
    }

    /// <summary>Verifies Right opens a nested menu and Escape restores focus to its owner row.</summary>
    [Fact]
    public void ContextMenu_NestedKeyboard_OpensAndRestoresOwnerFocus()
    {
        var parent = new ContextMenu(140f);
        var child = new ContextMenu(120f);
        child.AddItem("Child", () => { });
        parent.AddSubmenu("More", child);
        parent.BuildDrawList();
        var router = new UIEventRouter(parent, () => { });
        router.Focus(parent.Items[0]);

        router.RouteKey(new KeyInputEvent(InputKey.Right, true, false, InputModifiers.None));

        Assert.True(child.IsOpen);
        Assert.Same(child.Items[0], router.FocusedElement);

        router.RouteKey(new KeyInputEvent(InputKey.Escape, true, false, InputModifiers.None));

        Assert.False(child.IsOpen);
        Assert.Same(parent.Items[0], router.FocusedElement);
    }

    /// <summary>Verifies committed text selects matching rows and repeated letters cycle matches.</summary>
    [Fact]
    public void ContextMenu_TypeAhead_FocusesAndCyclesMatchingRows()
    {
        var menu = new ContextMenu(140f);
        menu.AddItem("Apple", () => { });
        menu.AddItem("Build", () => { });
        menu.AddItem("Browse", () => { });
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(menu.Items[0]);

        router.RouteText("b");
        Assert.Same(menu.Items[1], router.FocusedElement);
        router.RouteText("b");

        Assert.Same(menu.Items[2], router.FocusedElement);
    }

    /// <summary>Verifies Alt plus an ampersand mnemonic activates its menu action.</summary>
    [Fact]
    public void ContextMenu_Mnemonic_ActivatesMatchingAction()
    {
        var menu = new ContextMenu(140f);
        var saved = false;
        menu.AddItem("&Open", () => { });
        menu.AddItem("&Save", () => saved = true);
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(menu.Items[0]);

        router.RouteKey(new KeyInputEvent(InputKey.S, true, false, InputModifiers.Alt));

        Assert.True(saved);
        Assert.Equal("Save", menu.Items[1].LabelText);
    }

    /// <summary>Verifies separators occupy layout while keyboard navigation skips disabled rows.</summary>
    [Fact]
    public void ContextMenu_SeparatorAndDisabledItem_NavigationSkipsNonActions()
    {
        var menu = new ContextMenu(140f);
        menu.AddItem("First", () => { });
        menu.AddSeparator();
        menu.AddItem("Unavailable", () => { }, isEnabled: false);
        menu.AddItem("Last", () => { });
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(menu.Items[0]);

        router.RouteKey(new KeyInputEvent(InputKey.Down, true, false, InputModifiers.None));

        Assert.Same(menu.Items[2], router.FocusedElement);
        Assert.Equal(91f, menu.Height);
        Assert.Contains(menu.Children[0].Children, child => child is ContextMenuSeparator);
    }

    /// <summary>Verifies disabled rows cannot be selected by type-ahead or invoked by mnemonics.</summary>
    [Fact]
    public void ContextMenu_DisabledItem_TypeAheadAndMnemonicIgnoreIt()
    {
        var menu = new ContextMenu(140f);
        var invoked = false;
        menu.AddItem("&Build", () => invoked = true, isEnabled: false);
        menu.AddItem("Browse", () => { });
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(menu.Items[1]);

        router.RouteText("b");
        router.RouteKey(new KeyInputEvent(InputKey.B, true, false, InputModifiers.Alt));

        Assert.Same(menu.Items[1], router.FocusedElement);
        Assert.False(invoked);
    }

    /// <summary>Verifies check rows toggle state before invoking their callback.</summary>
    [Fact]
    public void ContextMenu_CheckItem_KeyboardTogglesState()
    {
        var menu = new ContextMenu(140f);
        bool? callbackState = null;
        var item = menu.AddCheckItem("Show Grid", false, value => callbackState = value);
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(item);

        router.RouteKey(new KeyInputEvent(InputKey.Enter, true, false, InputModifiers.None));

        Assert.True(item.IsChecked);
        Assert.True(callbackState);
    }

    /// <summary>Verifies radio rows enforce menu-local group exclusivity.</summary>
    [Fact]
    public void ContextMenu_RadioItems_SelectExclusively()
    {
        var menu = new ContextMenu(140f);
        var first = menu.AddRadioItem("Local", "space", true, () => { });
        var second = menu.AddRadioItem("World", "space", false, () => { });
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(second);

        router.RouteKey(new KeyInputEvent(InputKey.Space, true, false, InputModifiers.None));

        Assert.False(first.IsChecked);
        Assert.True(second.IsChecked);
        Assert.Equal("space", second.RadioGroup);
    }

    /// <summary>Verifies gesture accelerators are formatted and retained separately from action labels.</summary>
    [Fact]
    public void ContextMenu_AcceleratorText_UsesRightColumnPresentation()
    {
        var menu = new ContextMenu(180f);
        menu.AddItem("&Save", new UIKeyGesture(InputKey.S,
            InputModifiers.Control | InputModifiers.Shift), () => { });
        menu.BuildDrawList();

        Assert.Equal("Save", menu.Items[0].LabelText);
        Assert.Equal("Ctrl+Shift+S", menu.Items[0].AcceleratorText);
        Assert.Equal("Ctrl+Shift+S",
            new UIKeyGesture(InputKey.S, InputModifiers.Control | InputModifiers.Shift).ToDisplayString());
    }

    /// <summary>Verifies arbitrary retained icon elements occupy the menu icon column.</summary>
    [Fact]
    public void ContextMenu_Icon_RetainsAndArrangesElement()
    {
        var menu = new ContextMenu(180f);
        var icon = new Panel(Color.Green, 12f, 12f);
        menu.AddItem("Import", icon, () => { });

        menu.BuildDrawList();

        Assert.Same(icon, menu.Items[0].Icon);
        Assert.Same(menu.Items[0], icon.Parent?.Parent);
        Assert.True(icon.Width > 0f);
        Assert.True(icon.Left < menu.Items[0].Width * 0.5f);
    }

    /// <summary>Verifies pointer hover waits for host time before opening a submenu.</summary>
    [Fact]
    public void ContextMenu_SubmenuHover_OpensAfterDelay()
    {
        var parent = new ContextMenu(140f) { SubmenuOpenDelay = 0.25d };
        var child = new ContextMenu(120f);
        child.AddItem("Child", () => { });
        parent.AddSubmenu("More", child);
        parent.BuildDrawList();
        var router = new UIEventRouter(parent, () => { });

        router.MovePointer(new Vector2(20f, 12f));

        Assert.False(child.IsOpen);
        Assert.False(parent.AdvanceTime(0.2d));
        Assert.False(child.IsOpen);
        Assert.True(parent.AdvanceTime(0.1d));
        Assert.True(child.IsOpen);
    }

    /// <summary>Verifies leaving a submenu owner before its delay cancels opening.</summary>
    [Fact]
    public void ContextMenu_SubmenuHoverLeave_CancelsPendingOpen()
    {
        var parent = new ContextMenu(140f) { SubmenuOpenDelay = 0.25d };
        var child = new ContextMenu(120f);
        child.AddItem("Child", () => { });
        parent.AddSubmenu("More", child);
        parent.BuildDrawList();
        var router = new UIEventRouter(parent, () => { });

        router.MovePointer(new Vector2(20f, 12f));
        router.MovePointer(new Vector2(300f, 300f));
        parent.AdvanceTime(1d);

        Assert.False(child.IsOpen);
    }

    /// <summary>Verifies diagonal travel toward an open submenu delays accidental sibling switching.</summary>
    [Fact]
    public void ContextMenu_SubmenuCorridor_DefersSiblingOpen()
    {
        var parent = new ContextMenu(140f)
        {
            SubmenuOpenDelay = 0.1d,
            SubmenuCorridorDelay = 0.5d
        };
        var firstChild = new ContextMenu(120f);
        firstChild.AddItem("One", () => { });
        firstChild.AddItem("Two", () => { });
        firstChild.AddItem("Three", () => { });
        var secondChild = new ContextMenu(120f);
        secondChild.AddItem("Other", () => { });
        parent.AddSubmenu("First", firstChild);
        parent.AddSubmenu("Second", secondChild);
        parent.BuildDrawList();
        var router = new UIEventRouter(parent, () => { });

        router.MovePointer(new Vector2(20f, 12f));
        parent.AdvanceTime(0.11d);
        parent.BuildDrawList();
        Assert.True(firstChild.IsOpen);

        router.MovePointer(new Vector2(130f, 38f));
        parent.AdvanceTime(0.2d);

        Assert.True(firstChild.IsOpen);
        Assert.False(secondChild.IsOpen);
        parent.AdvanceTime(0.31d);
        Assert.False(firstChild.IsOpen);
        Assert.True(secondChild.IsOpen);
    }

    /// <summary>Verifies constrained menus scroll and keyboard focus keeps late rows visible.</summary>
    [Fact]
    public void ContextMenu_MaxVisibleHeight_ScrollsAndKeepsKeyboardItemVisible()
    {
        var menu = new ContextMenu(160f) { MaxVisibleHeight = 60f };
        for (var index = 0; index < 8; index++)
            menu.AddItem($"Item {index}", () => { });
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(menu.Items[0]);

        for (var index = 0; index < 7; index++)
            router.RouteKey(new KeyInputEvent(InputKey.Down, true, false, InputModifiers.None));

        Assert.Equal(60f, menu.Height);
        Assert.Same(menu.Items[7], router.FocusedElement);
        Assert.True(menu.ScrollOffset > 0f);

        var keyboardOffset = menu.ScrollOffset;
        menu.ScrollBy(-26f);
        Assert.True(menu.ScrollOffset < keyboardOffset);
    }

    /// <summary>Verifies Home/End and page navigation skip disabled rows and maintain visibility.</summary>
    [Fact]
    public void ContextMenu_HomeEndPage_NavigatesConstrainedRows()
    {
        var menu = new ContextMenu(160f) { MaxVisibleHeight = 60f };
        for (var index = 0; index < 8; index++)
            menu.AddItem($"Item {index}", () => { }, isEnabled: index is not 0 and not 6);
        menu.BuildDrawList();
        var router = new UIEventRouter(menu, () => { });
        router.Focus(menu.Items[3]);

        router.RouteKey(new KeyInputEvent(InputKey.Home, true, false, InputModifiers.None));
        Assert.Same(menu.Items[1], router.FocusedElement);

        router.RouteKey(new KeyInputEvent(InputKey.End, true, false, InputModifiers.None));
        Assert.Same(menu.Items[7], router.FocusedElement);
        Assert.True(menu.ScrollOffset > 0f);

        router.RouteKey(new KeyInputEvent(InputKey.PageUp, true, false, InputModifiers.None));
        Assert.Same(menu.Items[5], router.FocusedElement);
        router.RouteKey(new KeyInputEvent(InputKey.PageDown, true, false, InputModifiers.None));
        Assert.Same(menu.Items[7], router.FocusedElement);
    }

    /// <summary>Verifies an owned submenu flips left and shifts upward inside a constrained host.</summary>
    [Fact]
    public void ContextMenu_SubmenuNearHostEdges_FlipsAndClamps()
    {
        var root = new Canvas { Width = 220f, Height = 120f };
        var parent = new ContextMenu(90f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var child = new ContextMenu(100f);
        for (var index = 0; index < 4; index++)
            child.AddItem($"Child {index}", () => { });
        parent.AddSubmenu("More", child);
        root.Add(parent, new Vector2(130f, 80f));
        root.BuildDrawList();
        var router = new UIEventRouter(root, () => { });
        router.Focus(parent.Items[0]);

        router.RouteKey(new KeyInputEvent(InputKey.Right, true, false, InputModifiers.None));
        root.BuildDrawList();

        Assert.Equal(PopupPlacement.Left, child.ActualPlacement);
        Assert.True(child.Right <= parent.Left + 2f);
        Assert.True(child.Top >= root.Top);
        Assert.True(child.Bottom <= root.Bottom);
    }

    /// <summary>Verifies combo popup rows select values outside the collapsed header bounds.</summary>
    [Fact]
    public void ComboBox_PopupItemClick_SelectsAndClosesOverlay()
    {
        var combo = new ComboBox(120f, 30f);
        combo.SetItems(["One", "Two", "Three"]);
        combo.BuildDrawList();
        var router = new UIEventRouter(combo, () => { });
        Click(router, 20f, 15f);
        combo.BuildDrawList();
        Assert.True(combo.IsDropDownOpen);

        Click(router, 20f, 77f);

        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal("Two", combo.SelectedItem);
        Assert.False(combo.IsDropDownOpen);
    }

    /// <summary>Verifies focused combo headers support keyboard selection.</summary>
    [Fact]
    public void ComboBox_Keyboard_NavigatesSelection()
    {
        var combo = new ComboBox(120f, 30f);
        combo.SetItems(["One", "Two"]);
        combo.BuildDrawList();
        var router = new UIEventRouter(combo, () => { });
        Click(router, 20f, 15f);

        router.RouteKey(new KeyInputEvent(InputKey.Down, true, false, InputModifiers.None));
        router.RouteKey(new KeyInputEvent(InputKey.Down, true, false, InputModifiers.None));

        Assert.Equal(1, combo.SelectedIndex);
    }

    /// <summary>Verifies tab selection swaps retained content and keyboard navigation.</summary>
    [Fact]
    public void TabControl_SelectAndKeyboard_SwapsVisiblePage()
    {
        var tabs = new TabControl(240f, 120f);
        var first = new Panel(Color.Red);
        var second = new Panel(Color.Green);
        tabs.AddTab("First", first);
        tabs.AddTab("Second", second);
        tabs.BuildDrawList();
        Assert.True(first.IsVisible);
        Assert.False(second.IsVisible);
        var router = new UIEventRouter(tabs, () => { });
        Click(router, 150f, 15f);
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.False(first.IsVisible);
        Assert.True(second.IsVisible);

        router.RouteKey(new KeyInputEvent(InputKey.Left, true, false, InputModifiers.None));

        Assert.Equal(0, tabs.SelectedIndex);
    }

    /// <summary>Verifies toolbar items arrange horizontally with a semantic separator.</summary>
    [Fact]
    public void ToolBar_ItemsAndSeparator_ArrangeInHorizontalOrder()
    {
        var toolbar = new ToolBar(240f, 30f);
        var first = new Button(40f, 26f, "A");
        var second = new Button(40f, 26f, "B");
        toolbar.AddItem(first);
        toolbar.AddSeparator();
        toolbar.AddItem(second);

        var commands = toolbar.BuildDrawList().Commands;

        Assert.True(first.Left < second.Left);
        Assert.Contains(commands, command => command.Type == UIDrawCommandType.Line);
    }

    /// <summary>Performs one compatible primary click.</summary>
    /// <param name="router">Router receiving input.</param>
    /// <param name="x">Logical X.</param>
    /// <param name="y">Logical Y.</param>
    private static void Click(UIEventRouter router, float x, float y)
    {
        router.MovePointer(new Vector2(x, y));
        router.Press();
        router.Release(true);
    }
}
