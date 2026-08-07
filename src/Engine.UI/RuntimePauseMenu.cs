using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Provides a reusable full-screen runtime pause layer with resume and quit actions.</summary>
public sealed class RuntimePauseMenu : UIElement
{
    private readonly Panel _backdrop;
    private readonly StackPanel _menu;

    /// <summary>Gets the primary resume action for initial controller focus.</summary>
    public Button ResumeButton { get; }

    /// <summary>Gets the application quit action.</summary>
    public Button QuitButton { get; }

    /// <summary>Gets whether the pause layer is currently open.</summary>
    public bool IsOpen => IsVisible;

    /// <summary>Occurs when the player requests returning to gameplay.</summary>
    public event Action? ResumeRequested;

    /// <summary>Occurs when the player requests application closure.</summary>
    public event Action? QuitRequested;

    /// <summary>Creates a themed runtime pause layer.</summary>
    /// <param name="title">Displayed pause title.</param>
    /// <param name="theme">Theme supplying control visuals.</param>
    public RuntimePauseMenu(string title = "Paused", UITheme? theme = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var resolvedTheme = theme ?? UITheme.Dark;
        IsVisible = false;
        IsOverlay = true;
        _backdrop = new Panel(Color.Black) { Opacity = 0.72f };
        _menu = new StackPanel(320f, 220f, resolvedTheme.SurfaceRaised)
        {
            Padding = new Thickness(24f),
            Spacing = 14f
        };
        var heading = new Label(title, 272f, 42f)
        {
            FontSize = resolvedTheme.PanelTitleFontSize,
            ForegroundColor = resolvedTheme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false
        };
        ResumeButton = new Button(272f, 42f, "Resume", resolvedTheme, ButtonStyle.Primary);
        QuitButton = new Button(272f, 42f, "Quit", resolvedTheme);
        ResumeButton.Click += () => ResumeRequested?.Invoke();
        QuitButton.Click += () => QuitRequested?.Invoke();
        _menu.AddItem(heading);
        _menu.AddItem(ResumeButton);
        _menu.AddItem(QuitButton);
        AddChild(_backdrop);
        AddChild(_menu);
    }

    /// <summary>Shows the modal pause layer.</summary>
    public void Open()
    {
        if (IsVisible)
            return;
        IsVisible = true;
        InvalidateMeasure();
    }

    /// <summary>Hides the modal pause layer.</summary>
    public void Close()
    {
        if (!IsVisible)
            return;
        IsVisible = false;
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _backdrop.Measure(availableSize);
        _menu.Measure(new Vector2(320f, 220f));
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        _backdrop.Arrange(Vector2.Zero, contentSize);
        var menuSize = new Vector2(
            MathF.Min(320f, contentSize.X),
            MathF.Min(220f, contentSize.Y));
        _menu.Arrange((contentSize - menuSize) * 0.5f, menuSize);
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
    }
}
