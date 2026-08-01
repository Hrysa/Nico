using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies platform-specific title-bar control placement and styling.</summary>
public enum TitleBarStyle
{
    /// <summary>Selects the current operating system's conventional style.</summary>
    Auto,

    /// <summary>Uses left-aligned macOS traffic-light controls.</summary>
    MacOS,

    /// <summary>Uses right-aligned Windows-style controls.</summary>
    Windows
}

/// <summary>
/// Draws a borderless-window title bar with drag and window-control actions.
/// </summary>
public sealed class TitleBar : Surface
{
    /// <summary>Occurs when pointer dragging begins in an unoccupied title-bar region.</summary>
    public event Action? DragStarted;

    /// <summary>Occurs when the title bar is double-clicked.</summary>
    public event Action? MaximizeRequested;

    /// <summary>Occurs when the macOS green window control requests fullscreen.</summary>
    public event Action? FullScreenRequested;

    /// <summary>Occurs when the minimize control is clicked.</summary>
    public event Action? MinimizeRequested;

    /// <summary>Occurs when the close control is clicked.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Creates a custom title bar.
    /// </summary>
    /// <param name="width">Title-bar width.</param>
    /// <param name="height">Title-bar height.</param>
    /// <param name="title">Displayed project/window title.</param>
    /// <param name="theme">Theme supplying title-bar visuals.</param>
    /// <param name="style">Platform-specific control style.</param>
    public TitleBar(
        float width,
        float height,
        string title,
        UITheme? theme = null,
        TitleBarStyle style = TitleBarStyle.Auto)
        : base(0f, 0f, width, height, (theme ?? UITheme.Dark).Canvas, (theme ?? UITheme.Dark).Border)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        Name = "TitleBar";
        var titleLabel = new Label(width / 2f - 90f, 0f, 180f, height, title)
        {
            Name = "WindowTitle",
            FontSize = resolvedTheme.FontSize,
            ForegroundColor = resolvedTheme.TextPrimary,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        var resolvedStyle = style == TitleBarStyle.Auto
            ? OperatingSystem.IsMacOS() ? TitleBarStyle.MacOS : TitleBarStyle.Windows
            : style;
        UIElement minimize;
        UIElement maximize;
        UIElement close;
        if (resolvedStyle == TitleBarStyle.MacOS)
        {
            close = new MacWindowButton(8f, height, Color.FromSrgb(0xFF, 0x5F, 0x57), "×")
                { Name = "WindowClose" };
            minimize = new MacWindowButton(32f, height, Color.FromSrgb(0xFE, 0xBC, 0x2E), "−")
                { Name = "WindowMinimize" };
            maximize = new MacWindowButton(56f, height, Color.FromSrgb(0x28, 0xC8, 0x40), "+")
                { Name = "WindowMaximize" };
        }
        else
        {
            minimize = new Button(width - 108f, 0f, 36f, height, "−", resolvedTheme)
                { Name = "WindowMinimize", PaddingLeft = 13f };
            maximize = new Button(width - 72f, 0f, 36f, height, "□", resolvedTheme)
                { Name = "WindowMaximize", PaddingLeft = 12f };
            close = new Button(width - 36f, 0f, 36f, height, "×", resolvedTheme)
                { Name = "WindowClose", PaddingLeft = 12f, ForegroundColor = Color.FromSrgb(0xEC, 0x62, 0x5C) };
        }
        minimize.Click += () => MinimizeRequested?.Invoke();
        maximize.Click += () =>
        {
            if (resolvedStyle == TitleBarStyle.MacOS)
                FullScreenRequested?.Invoke();
            else
                MaximizeRequested?.Invoke();
        };
        close.Click += () => CloseRequested?.Invoke();
        AddChild(titleLabel);
        AddChild(minimize);
        AddChild(maximize);
        AddChild(close);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown()
    {
        DragStarted?.Invoke();
        base.OnMouseDown();
    }

    /// <inheritdoc/>
    protected override void OnDoubleClick()
    {
        MaximizeRequested?.Invoke();
        base.OnDoubleClick();
    }

    /// <summary>Draws one macOS traffic-light window control with a larger hit target.</summary>
    private sealed class MacWindowButton : UIElement
    {
        private readonly Color _color;
        private readonly string _symbol;

        /// <summary>Creates a macOS traffic-light control.</summary>
        /// <param name="x">Local X position.</param>
        /// <param name="titleBarHeight">Title-bar height.</param>
        /// <param name="color">Traffic-light color.</param>
        /// <param name="symbol">Symbol displayed on hover.</param>
        public MacWindowButton(float x, float titleBarHeight, Color color, string symbol)
            : base(x, 0f, 24f, titleBarHeight)
        {
            _color = color;
            _symbol = symbol;
        }

        /// <inheritdoc/>
        protected override void Paint(UIDrawList drawList)
        {
            const float Diameter = 13f;
            const float SymbolFontSize = 15f;
            var left = Left + (Width - Diameter) / 2f;
            var top = Top + (Height - Diameter) / 2f;
            var color = IsPressed ? Color.Lerp(_color, Color.Black, 0.22f) : _color;
            drawList.AddEllipse(left, top, left + Diameter, top + Diameter, color);
            if (IsHovered)
            {
                var symbolLeft = left + (_symbol == "×" ? 2.5f : 3f);
                drawList.AddText(_symbol, symbolLeft, top - 0.5f, SymbolFontSize,
                    Color.FromSrgb(0x4A, 0x32, 0x00), color);
            }
        }
    }
}
