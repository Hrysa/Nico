using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace UIShowcaseApp;

/// <summary>Builds an interactive gallery of the engine's reusable UI controls.</summary>
public static class UIShowcase
{
    private const float GalleryWidth = 1120f;
    private const float RowWidth = 1064f;
    private const float ControlHeight = 30f;

    /// <summary>Creates the gallery root and its host-local overlay.</summary>
    /// <param name="overlay">Overlay used by popups, tooltips, dialogs, and notifications.</param>
    /// <returns>The responsive showcase root.</returns>
    public static UIElement Create(out Canvas overlay)
    {
        var theme = UITheme.Dark;
        overlay = new Canvas { Name = "ShowcaseOverlay" };
        var status = CreateStatus(theme);
        var gallery = CreateGallery(theme, overlay, status);
        var menuBar = CreateMenuBar(theme, status);
        return new ShowcaseRoot(menuBar, gallery, status, overlay, theme);
    }

    /// <summary>Creates the window menu strip.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="status">Status label updated by actions.</param>
    /// <returns>The populated menu bar.</returns>
    private static MenuBar CreateMenuBar(UITheme theme, Label status)
    {
        var menuBar = new MenuBar(0f, 32f, theme) { Name = "Showcase menu" };
        var file = new ContextMenu(220f, theme);
        file.AddItem("New example", () => SetStatus(status, "New example selected"));
        file.AddItem("Open…", new UIKeyGesture(InputKey.O, InputModifiers.Control),
            () => SetStatus(status, "Open selected"));
        file.AddSeparator();
        file.AddItem("Disabled command", () => { }, isEnabled: false);
        var view = new ContextMenu(220f, theme);
        view.AddItem("Reset gallery", () => SetStatus(status, "Gallery reset requested"));
        view.AddItem("Toggle diagnostics", () => SetStatus(status, "Diagnostics toggled"));
        menuBar.AddMenu("File", file);
        menuBar.AddMenu("View", view);
        return menuBar;
    }

    /// <summary>Creates the scrollable vertical gallery.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="overlay">Host overlay.</param>
    /// <param name="status">Status label updated by interactions.</param>
    /// <returns>The gallery stack.</returns>
    private static StackPanel CreateGallery(UITheme theme, Canvas overlay, Label status)
    {
        var gallery = new StackPanel(GalleryWidth, 0f, theme.Canvas)
        {
            Name = "Component gallery",
            Padding = new Thickness(18f),
            Spacing = 16f
        };
        gallery.AddItem(new TextBlock(
            "Interactive reference for runtime and editor UI. Use Tab and arrow keys to verify keyboard navigation.",
            RowWidth, 42f)
        {
            FontSize = 15f,
            ForegroundColor = theme.TextSecondary,
            Wrapping = TextWrapMode.Wrap
        });
        gallery.AddItem(CreateButtonsSection(theme, overlay, status));
        gallery.AddItem(CreateSelectionSection(theme, status));
        gallery.AddItem(CreateTextSection(theme, status));
        gallery.AddItem(CreateRangeSection(theme, status));
        gallery.AddItem(CreateCollectionsSection(theme, status));
        gallery.AddItem(CreateNavigationSection(theme, status));
        gallery.AddItem(CreateFeedbackSection(theme, overlay, status));
        gallery.AddItem(CreateLayoutSection(theme));
        return gallery;
    }

    /// <summary>Creates buttons, icons, and command controls.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="overlay">Host overlay.</param>
    /// <param name="status">Status label.</param>
    /// <returns>The completed section.</returns>
    private static ShowcaseSection CreateButtonsSection(
        UITheme theme, Canvas overlay, Label status)
    {
        var section = new ShowcaseSection("Buttons and commands", theme);
        var primary = new Button(132f, ControlHeight, "Primary", theme, ButtonStyle.Primary);
        primary.Click += () => SetStatus(status, "Primary button clicked");
        var subtle = new Button(132f, ControlHeight, "Subtle", theme);
        subtle.Click += () => SetStatus(status, "Subtle button clicked");
        var disabled = new Button(132f, ControlHeight, "Disabled", theme) { IsEnabled = false };
        var repeat = new RepeatButton(132f, ControlHeight, "Hold to repeat", theme);
        repeat.Click += () => SetStatus(status, "Repeat button tick");
        section.AddRow("Button states", primary, subtle, disabled, repeat);

        var iconRow = new ShowcaseRow(RowWidth, 34f, "Vector icons", theme);
        iconRow.AddControl(new Icon(IconKind.Check, 20f));
        iconRow.AddControl(new Icon(IconKind.ChevronRight, 20f));
        iconRow.AddControl(new Icon(IconKind.ChevronDown, 20f));
        iconRow.AddControl(new Icon(IconKind.Close, 20f));
        iconRow.AddControl(new Icon(IconKind.Plus, 20f));
        iconRow.AddControl(new Icon(IconKind.Minus, 20f));
        iconRow.AddControl(new Icon(IconKind.Search, 20f));
        section.AddRow(iconRow);

        var tipOwner = new Button(180f, ControlHeight, "Hover for tooltip", theme);
        _ = new ToolTip(tipOwner, overlay, "Native-style delayed tooltip", theme)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var contextButton = new Button(180f, ControlHeight, "Open context menu", theme);
        var contextMenu = new ContextMenu(210f, theme)
        {
            Owner = contextButton,
            Placement = PopupPlacement.Below,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        contextMenu.AddItem("Rename", () => SetStatus(status, "Rename selected"));
        contextMenu.AddItem("Duplicate", () => SetStatus(status, "Duplicate selected"));
        contextMenu.AddSeparator();
        contextMenu.AddItem("Delete", () => SetStatus(status, "Delete selected"));
        contextMenu.Close();
        overlay.Add(contextMenu, Vector2.Zero);
        contextButton.Click += () =>
        {
            contextMenu.Open();
            overlay.PlacePopup(contextMenu);
        };
        section.AddRow("Transient surfaces", tipOwner, contextButton);
        return section;
    }

    /// <summary>Creates toggles and selection controls.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="status">Status label.</param>
    /// <returns>The completed section.</returns>
    private static ShowcaseSection CreateSelectionSection(UITheme theme, Label status)
    {
        var section = new ShowcaseSection("Selection controls", theme);
        var check = new CheckBox(170f, ControlHeight, "Enable shadows", theme)
        {
            IsChecked = true
        };
        var toggle = new ToggleButton(145f, ControlHeight, "Toggle button", theme);
        var toggleSwitch = new ToggleSwitch(48f, 24f, theme) { IsChecked = true };
        check.CheckedChanged += value => SetStatus(status, $"Checkbox: {value}");
        toggle.CheckedChanged += value => SetStatus(status, $"Toggle: {value}");
        toggleSwitch.CheckedChanged += value => SetStatus(status, $"Switch: {value}");
        section.AddRow("Independent", check, toggle, toggleSwitch);

        var radioA = new RadioButton(130f, ControlHeight, "Quality A", theme)
        {
            GroupName = "quality",
            IsChecked = true
        };
        var radioB = new RadioButton(130f, ControlHeight, "Quality B", theme)
        {
            GroupName = "quality"
        };
        var radioC = new RadioButton(130f, ControlHeight, "Quality C", theme)
        {
            GroupName = "quality"
        };
        section.AddRow("Exclusive group", radioA, radioB, radioC);

        var combo = new ComboBox(220f, ControlHeight, theme) { Name = "Rendering backend" };
        combo.SetItems(["Vulkan", "Direct3D 12", "Metal"]);
        combo.Select(0);
        combo.SelectionChanged += (_, value) => SetStatus(status, $"Combo box: {value}");
        section.AddRow("Choice popup", combo);
        return section;
    }

    /// <summary>Creates text display and editing controls.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="status">Status label.</param>
    /// <returns>The completed section.</returns>
    private static ShowcaseSection CreateTextSection(UITheme theme, Label status)
    {
        var section = new ShowcaseSection("Text, Unicode, and IME", theme);
        section.AddRow("Typography",
            new Label("Latin · العربية · עברית · 日本語 · 😀", 420f, ControlHeight)
            {
                ForegroundColor = theme.TextPrimary
            });

        var field = new TextField(260f, ControlHeight, theme)
        {
            Name = "Name field",
            Placeholder = "Enter text with your system IME"
        };
        field.TextChanged += value => SetStatus(status, $"Text changed: {value}");
        var password = new PasswordField(220f, ControlHeight, theme)
        {
            Name = "Password field",
            Placeholder = "Password",
            Text = "secret"
        };
        var readOnly = new TextField(220f, ControlHeight, theme)
        {
            Text = "Read-only value",
            IsReadOnly = true
        };
        section.AddRow("Single-line editors", field, password, readOnly);

        var editor = new TextBox(660f, 104f, theme)
        {
            Name = "Multiline editor",
            Text = "Multiline editing, selection, undo/redo, and scrolling.\n"
                + "Mixed direction: Engine المحرّك 123 מנוע. 啊啊啊\n"
                + "Graphemes: 👨‍👩‍👧‍👦  é  🇨🇳"
        };
        section.AddRow(new ShowcaseRow(RowWidth, 108f, "Multiline editor", theme, editor));
        return section;
    }

    /// <summary>Creates range and progress controls.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="status">Status label.</param>
    /// <returns>The completed section.</returns>
    private static ShowcaseSection CreateRangeSection(UITheme theme, Label status)
    {
        var section = new ShowcaseSection("Ranges and progress", theme);
        var slider = new Slider(UIOrientation.Horizontal, 300f, 24f, theme)
        {
            Minimum = 0f,
            Maximum = 100f,
            Value = 42f,
            Name = "Volume"
        };
        var numeric = new NumericField(170f, ControlHeight, theme)
        {
            Minimum = -100d,
            Maximum = 100d,
            Value = 12.5d,
            Step = 0.5d,
            FormatString = "0.0"
        };
        slider.ValueChanged += value => SetStatus(status, $"Slider: {value:0}");
        numeric.ValueChanged += value => SetStatus(status, $"Numeric field: {value:0.0}");
        section.AddRow("Editable values", slider, numeric);

        section.AddRow("Progress",
            new ProgressBar(300f, 12f, theme) { Value = 0.68f },
            new ProgressBar(300f, 12f, theme) { IsIndeterminate = true });
        return section;
    }

    /// <summary>Creates list and tree collection controls.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="status">Status label.</param>
    /// <returns>The completed section.</returns>
    private static ShowcaseSection CreateCollectionsSection(UITheme theme, Label status)
    {
        var section = new ShowcaseSection("Collections and virtualization", theme);
        var list = new ListView(360f, 150f, theme)
        {
            Name = "Asset list",
            SelectionMode = UISelectionMode.Extended
        };
        list.SetItems(["Scene.scene", "Player.cs", "Materials", "Textures", "Audio", "Scripts"]);
        list.Select(0);
        list.SelectionChanged += (_, value) => SetStatus(status, $"List: {value}");

        var root = new Node { Name = "World" };
        var camera = new Node { Name = "Main Camera" };
        var environment = new Node { Name = "Environment" };
        environment.AddChild(new Node { Name = "Sun" });
        environment.AddChild(new Node { Name = "Ground" });
        root.AddChild(camera);
        root.AddChild(environment);
        var tree = new TreeView(430f, 150f, theme) { Name = "Scene hierarchy" };
        tree.SetRoots([root]);
        tree.SelectionChanged += node => SetStatus(status, $"Tree: {node?.Name ?? "none"}");
        section.AddRow(new ShowcaseRow(RowWidth, 154f, "Virtualized views", theme, list, tree));
        return section;
    }

    /// <summary>Creates tabs, toolbar, and structural navigation controls.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="status">Status label.</param>
    /// <returns>The completed section.</returns>
    private static ShowcaseSection CreateNavigationSection(UITheme theme, Label status)
    {
        var section = new ShowcaseSection("Navigation and composition", theme);
        var toolbar = new ToolBar(660f, 34f, theme);
        var select = new Button(76f, 28f, "Select", theme, ButtonStyle.Primary);
        var move = new Button(70f, 28f, "Move", theme);
        var rotate = new Button(76f, 28f, "Rotate", theme);
        select.Click += () => SetStatus(status, "Select tool active");
        move.Click += () => SetStatus(status, "Move tool active");
        rotate.Click += () => SetStatus(status, "Rotate tool active");
        toolbar.AddItem(select);
        toolbar.AddSeparator(theme);
        toolbar.AddItem(move);
        toolbar.AddItem(rotate);
        section.AddRow("Toolbar", toolbar);

        var tabs = new TabControl(760f, 130f, 30f, theme) { Name = "Document tabs" };
        tabs.AddTab("Scene", CreateTabPage("Scene tab content", theme));
        tabs.AddTab("Game", CreateTabPage("Game tab content", theme));
        tabs.AddTab("Profiler", CreateTabPage("Profiler tab content", theme));
        tabs.SelectionChanged += (_, item) => SetStatus(status, $"Tab: {item?.Header}");
        section.AddRow(new ShowcaseRow(RowWidth, 134f, "Tabs", theme, tabs));
        return section;
    }

    /// <summary>Creates overlay notification and dialog examples.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="overlay">Host overlay.</param>
    /// <param name="status">Status label.</param>
    /// <returns>The completed section.</returns>
    private static ShowcaseSection CreateFeedbackSection(
        UITheme theme, Canvas overlay, Label status)
    {
        var section = new ShowcaseSection("Feedback and overlays", theme);
        var toasts = new ToastHost(theme);
        overlay.Add(toasts, Vector2.Zero);
        var toastButton = new Button(150f, ControlHeight, "Show toast", theme);
        toastButton.Click += () =>
        {
            toasts.Show("Showcase notification", ToastSeverity.Success, 4d,
                "Action", () => SetStatus(status, "Toast action invoked"));
            SetStatus(status, "Toast shown");
        };

        var modal = CreateModal(theme, status);
        modal.IsVisible = false;
        overlay.Add(modal, Vector2.Zero);
        var modalButton = new Button(150f, ControlHeight, "Open dialog", theme);
        modalButton.Click += () =>
        {
            modal.IsVisible = true;
            modal.InvalidateMeasure();
            SetStatus(status, "Dialog opened");
        };
        modal.DismissRequested += () => modal.IsVisible = false;
        section.AddRow("Host overlays", toastButton, modalButton);
        return section;
    }

    /// <summary>Creates layout-container examples and graphics-backed placeholders.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <returns>The completed section.</returns>
    private static ShowcaseSection CreateLayoutSection(UITheme theme)
    {
        var section = new ShowcaseSection("Layout and specialized surfaces", theme);
        var grid = new Grid(theme.SurfaceRaised) { Width = 520f, Height = 84f };
        grid.Columns.Add(GridLength.Star(1f));
        grid.Columns.Add(GridLength.Star(2f));
        grid.Rows.Add(GridLength.Pixels(42f));
        grid.Rows.Add(GridLength.Pixels(42f));
        grid.Add(CreateCell("1×", theme), 0, 0);
        grid.Add(CreateCell("2× flexible", theme), 0, 1);
        grid.Add(CreateCell("Grid row 2", theme), 1, 0, columnSpan: 2);
        section.AddRow(new ShowcaseRow(RowWidth, 88f, "Grid tracks", theme, grid));

        var viewport = new ViewportPanel(300f, 84f, theme.SurfaceRaised)
        {
            Name = "Viewport placeholder"
        };
        var note = new TextBlock(
            "ViewportPanel is shown without a registered render view. Image requires a renderer-owned TextureHandle.",
            470f, 72f)
        {
            ForegroundColor = theme.TextSecondary,
            Wrapping = TextWrapMode.Wrap
        };
        section.AddRow(new ShowcaseRow(RowWidth, 88f, "Graphics-backed", theme, viewport, note));
        return section;
    }

    /// <summary>Creates a centered sample modal.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="status">Status label.</param>
    /// <returns>The modal.</returns>
    private static Modal CreateModal(UITheme theme, Label status)
    {
        var modal = new Modal(1f, 1f, 420f, 190f, theme) { Name = "Example dialog" };
        modal.Dialog.Width = 420f;
        modal.Dialog.Height = 190f;
        var content = new StackPanel(0f, 0f, theme.SurfaceRaised)
        {
            Padding = new Thickness(18f),
            Spacing = 14f
        };
        content.AddItem(new DialogHeader(
            "Example dialog", "Modal focus and backdrop behavior", theme));
        content.AddItem(new TextBlock(
            "This dialog is hosted in the same overlay as popups and notifications.", 360f, 42f)
        {
            ForegroundColor = theme.TextPrimary,
            Wrapping = TextWrapMode.Wrap
        });
        var close = new Button(100f, ControlHeight, "Close", theme, ButtonStyle.Primary);
        close.Click += () =>
        {
            modal.IsVisible = false;
            SetStatus(status, "Dialog closed");
        };
        content.AddItem(close);
        modal.Dialog.AddChild(content);
        return modal;
    }

    /// <summary>Creates one tab page.</summary>
    /// <param name="text">Page label.</param>
    /// <param name="theme">Showcase theme.</param>
    /// <returns>The page surface.</returns>
    private static UIElement CreateTabPage(string text, UITheme theme)
    {
        return new Label(text)
        {
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 14f
        };
    }

    /// <summary>Creates one labeled grid cell.</summary>
    /// <param name="text">Cell label.</param>
    /// <param name="theme">Showcase theme.</param>
    /// <returns>The cell element.</returns>
    private static UIElement CreateCell(string text, UITheme theme)
    {
        return new Label(text)
        {
            ForegroundColor = theme.TextPrimary,
            BackgroundColor = theme.Surface,
            PaintBackground = true,
            PaddingLeft = 10f,
            Margin = new Thickness(2f)
        };
    }

    /// <summary>Creates the persistent interaction-status label.</summary>
    /// <param name="theme">Showcase theme.</param>
    /// <returns>The status label.</returns>
    private static Label CreateStatus(UITheme theme) => new("Ready", 0f, 28f)
    {
        Name = "Interaction status",
        BackgroundColor = theme.SurfaceRaised,
        PaintBackground = true,
        ForegroundColor = theme.TextSecondary,
        PaddingLeft = 12f,
        IsHitTestVisible = false
    };

    /// <summary>Updates the persistent interaction status.</summary>
    /// <param name="status">Status label.</param>
    /// <param name="message">New message.</param>
    private static void SetStatus(Label status, string message) => status.Text = message;
}

/// <summary>Arranges the menu, scrolling gallery, status strip, and overlay.</summary>
internal sealed class ShowcaseRoot : Panel
{
    private readonly MenuBar _menuBar;
    private readonly ScrollViewer _scrollViewer;
    private readonly Label _status;
    private readonly Canvas _overlay;

    /// <summary>Creates the responsive showcase root.</summary>
    /// <param name="menuBar">Window menu strip.</param>
    /// <param name="gallery">Scrollable gallery content.</param>
    /// <param name="status">Bottom status strip.</param>
    /// <param name="overlay">Full-window overlay.</param>
    /// <param name="theme">Showcase theme.</param>
    internal ShowcaseRoot(
        MenuBar menuBar, StackPanel gallery, Label status, Canvas overlay, UITheme theme)
        : base(theme.Canvas)
    {
        Name = "UI showcase";
        _menuBar = menuBar;
        _status = status;
        _overlay = overlay;
        _scrollViewer = new ScrollViewer(theme: theme)
        {
            Name = "Gallery scroll viewer",
            Content = gallery,
            CanScrollHorizontally = true,
            CanScrollVertically = true
        };
        AddChild(_menuBar);
        AddChild(_scrollViewer);
        AddChild(_status);
        AddChild(_overlay);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        const float menuHeight = 32f;
        const float statusHeight = 28f;
        var bodyHeight = MathF.Max(0f, availableSize.Y - menuHeight - statusHeight);
        _menuBar.Measure(new Vector2(availableSize.X, menuHeight));
        _scrollViewer.Measure(new Vector2(availableSize.X, bodyHeight));
        _status.Measure(new Vector2(availableSize.X, statusHeight));
        _overlay.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        const float menuHeight = 32f;
        const float statusHeight = 28f;
        var bodyHeight = MathF.Max(0f, contentSize.Y - menuHeight - statusHeight);
        _menuBar.Arrange(Vector2.Zero, new Vector2(contentSize.X, menuHeight));
        _scrollViewer.Arrange(new Vector2(0f, menuHeight),
            new Vector2(contentSize.X, bodyHeight));
        _status.Arrange(new Vector2(0f, menuHeight + bodyHeight),
            new Vector2(contentSize.X, statusHeight));
        _overlay.Arrange(Vector2.Zero, contentSize);
    }
}

/// <summary>Groups related showcase rows on a raised surface.</summary>
internal sealed class ShowcaseSection : StackPanel
{
    private readonly UITheme _theme;

    /// <summary>Creates a titled showcase section.</summary>
    /// <param name="title">Section title.</param>
    /// <param name="theme">Showcase theme.</param>
    internal ShowcaseSection(string title, UITheme theme)
        : base(0f, 0f, theme.Surface)
    {
        _theme = theme;
        Padding = new Thickness(14f);
        Spacing = 10f;
        AddItem(new SectionHeader(1064f, title, theme));
    }

    /// <summary>Adds a prebuilt row.</summary>
    /// <param name="row">Row to append.</param>
    internal void AddRow(ShowcaseRow row) => AddItem(row);

    /// <summary>Creates and adds a standard-height labeled row.</summary>
    /// <param name="label">Row label.</param>
    /// <param name="controls">Controls displayed from left to right.</param>
    internal void AddRow(string label, params UIElement[] controls) =>
        AddItem(new ShowcaseRow(1064f, 38f, label, _theme, controls));
}

/// <summary>Arranges a row label followed by horizontally spaced controls.</summary>
internal sealed class ShowcaseRow : UIElement
{
    private const float LabelWidth = 176f;
    private const float Gap = 12f;
    private readonly Label _label;
    private readonly List<UIElement> _controls = [];

    /// <summary>Creates an empty labeled row.</summary>
    /// <param name="width">Row width.</param>
    /// <param name="height">Row height.</param>
    /// <param name="label">Row label.</param>
    /// <param name="theme">Showcase theme.</param>
    internal ShowcaseRow(float width, float height, string label, UITheme theme)
        : base(width, height)
    {
        _label = new Label(label, LabelWidth, height)
        {
            ForegroundColor = theme.TextSecondary,
        };
        AddChild(_label);
    }

    /// <summary>Creates a row populated with controls.</summary>
    /// <param name="width">Row width.</param>
    /// <param name="height">Row height.</param>
    /// <param name="label">Row label.</param>
    /// <param name="theme">Showcase theme.</param>
    /// <param name="controls">Initial controls.</param>
    internal ShowcaseRow(
        float width, float height, string label, UITheme theme, params UIElement[] controls)
        : this(width, height, label, theme)
    {
        for (var index = 0; index < controls.Length; index++)
            AddControl(controls[index]);
    }

    /// <summary>Adds one control to the horizontal row.</summary>
    /// <param name="control">Control to append.</param>
    internal void AddControl(UIElement control)
    {
        ArgumentNullException.ThrowIfNull(control);
        _controls.Add(control);
        AddChild(control);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _label.Measure(new Vector2(LabelWidth, availableSize.Y));
        for (var index = 0; index < _controls.Count; index++)
            _controls[index].Measure(new Vector2(float.PositiveInfinity, availableSize.Y));
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        _label.Arrange(Vector2.Zero, new Vector2(LabelWidth, contentSize.Y));
        var x = LabelWidth + Gap;
        for (var index = 0; index < _controls.Count; index++)
        {
            var control = _controls[index];
            var size = control.DesiredSize;
            var height = MathF.Min(contentSize.Y, size.Y);
            control.Arrange(new Vector2(x, MathF.Max(0f, (contentSize.Y - height) / 2f)),
                new Vector2(size.X, height));
            x += size.X + Gap;
        }
    }
}
