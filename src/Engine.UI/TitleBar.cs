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
    /// <summary>Gets the standard editor title-bar height.</summary>
    public const float DefaultHeight = 30f;

    private readonly TitleBarStyle _resolvedStyle;

    /// <summary>Gets the left-aligned content zone.</summary>
    public Panel LeftZone { get; }

    /// <summary>Gets the horizontally centered content zone.</summary>
    public Panel CenterZone { get; }

    /// <summary>Gets the right-aligned content zone.</summary>
    public Panel RightZone { get; }

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
    /// <param name="theme">Theme supplying title-bar visuals.</param>
    /// <param name="style">Platform-specific control style.</param>
    public TitleBar(
        float width,
        float height,
        UITheme? theme = null,
        TitleBarStyle style = TitleBarStyle.Auto)
        : base((theme ?? UITheme.Dark).Canvas, (theme ?? UITheme.Dark).Border, width, height)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        Name = "TitleBar";
        BorderThickness = 0f;
        _resolvedStyle = style == TitleBarStyle.Auto
            ? OperatingSystem.IsMacOS() ? TitleBarStyle.MacOS : TitleBarStyle.Windows
            : style;
        LeftZone = new TitleBarZone(HorizontalAlignment.Left) { Name = "TitleBarLeft" };
        CenterZone = new TitleBarZone(HorizontalAlignment.Center) { Name = "TitleBarCenter" };
        RightZone = new TitleBarZone(HorizontalAlignment.Right) { Name = "TitleBarRight" };
        AddChild(LeftZone);
        AddChild(CenterZone);
        AddChild(RightZone);

        UIElement minimize;
        UIElement maximize;
        UIElement close;
        if (_resolvedStyle == TitleBarStyle.MacOS)
        {
            close = new MacWindowButton(height, Color.FromSrgb(0xFF, 0x5F, 0x57), "×")
                { Name = "WindowClose" };
            minimize = new MacWindowButton(height, Color.FromSrgb(0xFE, 0xBC, 0x2E), "−")
                { Name = "WindowMinimize" };
            maximize = new MacWindowButton(height, Color.FromSrgb(0x28, 0xC8, 0x40), "+")
                { Name = "WindowMaximize" };
        }
        else
        {
            const float WindowControlGlyphSize = 30f;
            var minimizeButton = new Button(36f, height, resolvedTheme)
                { Name = "WindowMinimize", Padding = new Thickness(3f, 0f), CornerRadius = 0f };
            var maximizeButton = new Button(36f, height, resolvedTheme)
                { Name = "WindowMaximize", Padding = new Thickness(3f, 0f), CornerRadius = 0f };
            var closeButton = new Button(36f, height, resolvedTheme)
                { Name = "WindowClose", Padding = new Thickness(3f, 0f), CornerRadius = 0f,
                    ForegroundColor = Color.White,
                    InteractionColors = resolvedTheme.GetButtonStyle(ButtonStyle.Subtle)
                        .InteractionColors with
                        {
                            Hovered = Color.FromSrgb(0xE8, 0x11, 0x23),
                            Pressed = Color.FromSrgb(0xC5, 0x0F, 0x1F)
                        } };
            minimizeButton.Content = new WindowsWindowGlyph(
                WindowControlGlyph.Minimize, WindowControlGlyphSize, resolvedTheme.TextPrimary);
            maximizeButton.Content = new WindowsWindowGlyph(
                WindowControlGlyph.Maximize, WindowControlGlyphSize, resolvedTheme.TextPrimary);
            closeButton.Content = new WindowsWindowGlyph(
                WindowControlGlyph.Close, WindowControlGlyphSize, Color.White);
            minimize = minimizeButton;
            maximize = maximizeButton;
            close = closeButton;
        }
        minimize.Click += () => MinimizeRequested?.Invoke();
        maximize.Click += () =>
        {
            if (_resolvedStyle == TitleBarStyle.MacOS)
                FullScreenRequested?.Invoke();
            else
                MaximizeRequested?.Invoke();
        };
        close.Click += () => CloseRequested?.Invoke();
        var windowZone = _resolvedStyle == TitleBarStyle.MacOS ? LeftZone : RightZone;
        if (_resolvedStyle == TitleBarStyle.MacOS)
        {
            windowZone.AddChild(close);
            windowZone.AddChild(minimize);
            windowZone.AddChild(maximize);
        }
        else
        {
            windowZone.AddChild(minimize);
            windowZone.AddChild(maximize);
            windowZone.AddChild(close);
        }
        Measure(new System.Numerics.Vector2(width, height));
        Arrange(System.Numerics.Vector2.Zero, new System.Numerics.Vector2(width, height));
    }

    /// <summary>Identifies a Windows caption-button symbol.</summary>
    private enum WindowControlGlyph
    {
        Minimize,
        Maximize,
        Close
    }

    /// <summary>Paints a Windows caption symbol from centered vector strokes.</summary>
    private sealed class WindowsWindowGlyph : UIElement
    {
        private readonly WindowControlGlyph _glyph;
        private readonly Color _color;

        /// <summary>Creates a fixed-size, non-interactive caption symbol.</summary>
        /// <param name="glyph">Symbol to paint.</param>
        /// <param name="size">Square content size.</param>
        /// <param name="color">Stroke color.</param>
        public WindowsWindowGlyph(WindowControlGlyph glyph, float size, Color color)
            : base(size, size)
        {
            _glyph = glyph;
            _color = color;
            IsHitTestVisible = false;
        }

        /// <inheritdoc/>
        protected override void Paint(UIDrawList drawList)
        {
            const float HalfExtent = 5f;
            const float StrokeWidth = 1.25f;
            var centerX = Left + Width * 0.5f;
            var centerY = Top + Height * 0.5f;
            switch (_glyph)
            {
                case WindowControlGlyph.Minimize:
                    drawList.AddLine(centerX - HalfExtent, centerY,
                        centerX + HalfExtent, centerY, StrokeWidth, _color);
                    break;
                case WindowControlGlyph.Maximize:
                    drawList.AddLine(centerX - HalfExtent, centerY - HalfExtent,
                        centerX + HalfExtent, centerY - HalfExtent, StrokeWidth, _color);
                    drawList.AddLine(centerX + HalfExtent, centerY - HalfExtent,
                        centerX + HalfExtent, centerY + HalfExtent, StrokeWidth, _color);
                    drawList.AddLine(centerX + HalfExtent, centerY + HalfExtent,
                        centerX - HalfExtent, centerY + HalfExtent, StrokeWidth, _color);
                    drawList.AddLine(centerX - HalfExtent, centerY + HalfExtent,
                        centerX - HalfExtent, centerY - HalfExtent, StrokeWidth, _color);
                    break;
                case WindowControlGlyph.Close:
                    drawList.AddLine(centerX - HalfExtent, centerY - HalfExtent,
                        centerX + HalfExtent, centerY + HalfExtent, StrokeWidth, _color);
                    drawList.AddLine(centerX + HalfExtent, centerY - HalfExtent,
                        centerX - HalfExtent, centerY + HalfExtent, StrokeWidth, _color);
                    break;
            }
        }
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(System.Numerics.Vector2 contentSize)
    {
        var zoneWidth = contentSize.X / 3f;
        LeftZone.Arrange(System.Numerics.Vector2.Zero,
            new System.Numerics.Vector2(zoneWidth, contentSize.Y));
        CenterZone.Arrange(new System.Numerics.Vector2(zoneWidth, 0f),
            new System.Numerics.Vector2(zoneWidth, contentSize.Y));
        RightZone.Arrange(new System.Numerics.Vector2(zoneWidth * 2f, 0f),
            new System.Numerics.Vector2(contentSize.X - zoneWidth * 2f, contentSize.Y));
    }

    /// <summary>Arranges a title-bar zone's children as one aligned horizontal group.</summary>
    private sealed class TitleBarZone : Panel
    {
        private const float EdgeInset = 8f;
        private readonly HorizontalAlignment _contentAlignment;

        /// <summary>Creates an aligned title-bar content zone.</summary>
        /// <param name="contentAlignment">Alignment of the child group within the zone.</param>
        public TitleBarZone(HorizontalAlignment contentAlignment)
            : base()
        {
            _contentAlignment = contentAlignment;
            IsHitTestVisible = false;
        }

        /// <inheritdoc/>
        protected override System.Numerics.Vector2 MeasureOverride(System.Numerics.Vector2 availableSize)
        {
            var desiredWidth = 0f;
            var desiredHeight = 0f;
            var children = Children;
            for (var index = 0; index < children.Count; index++)
            {
                if (children[index] is not UIElement child)
                    continue;
                child.Measure(availableSize);
                desiredWidth += child.DesiredSize.X;
                desiredHeight = MathF.Max(desiredHeight, child.DesiredSize.Y);
            }
            return new System.Numerics.Vector2(desiredWidth, desiredHeight);
        }

        /// <inheritdoc/>
        protected override void ArrangeOverride(System.Numerics.Vector2 contentSize)
        {
            var children = Children;
            var groupWidth = 0f;
            for (var index = 0; index < children.Count; index++)
            {
                if (children[index] is UIElement child)
                    groupWidth += child.DesiredSize.X;
            }
            var x = _contentAlignment switch
            {
                HorizontalAlignment.Center => (contentSize.X - groupWidth) / 2f,
                HorizontalAlignment.Right => contentSize.X - groupWidth,
                _ => EdgeInset
            };
            for (var index = 0; index < children.Count; index++)
            {
                if (children[index] is not UIElement child)
                    continue;
                var childHeight = MathF.Min(contentSize.Y, child.DesiredSize.Y);
                var y = (contentSize.Y - childHeight) / 2f;
                child.Arrange(new System.Numerics.Vector2(x, y),
                    new System.Numerics.Vector2(child.DesiredSize.X, childHeight));
                x += child.DesiredSize.X;
            }
        }
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
        /// <param name="titleBarHeight">Title-bar height.</param>
        /// <param name="color">Traffic-light color.</param>
        /// <param name="symbol">Symbol displayed on hover.</param>
        public MacWindowButton(float titleBarHeight, Color color, string symbol)
            : base(24f, titleBarHeight)
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
