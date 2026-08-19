using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>An owner-bound delayed tooltip hosted by an overlay canvas.</summary>
public sealed class ToolTip : Popup, IDisposable
{
    private readonly Canvas _overlay;
    private readonly Label _label;
    private bool _pending;
    private double _elapsed;
    private bool _disposed;

    /// <summary>Gets or sets hover delay in seconds.</summary>
    public double Delay { get; set; } = 0.5;

    /// <summary>Gets or sets offset from the owner's lower-left corner.</summary>
    public Vector2 Offset { get; set; } = new(0f, 6f);

    /// <summary>Gets or sets displayed tooltip text.</summary>
    public string Text
    {
        get => _label.Text;
        set
        {
            _label.Text = value ?? string.Empty;
            ResizeToText();
        }
    }

    /// <summary>Creates and attaches a tooltip to an overlay canvas.</summary>
    /// <param name="owner">Element whose hover state controls the tooltip.</param>
    /// <param name="overlay">Host overlay canvas.</param>
    /// <param name="text">Displayed text.</param>
    /// <param name="theme">Theme supplying popup colors.</param>
    public ToolTip(UIElement owner, Canvas overlay, string text, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).SurfaceRaised, (theme ?? UITheme.Dark).BorderStrong)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(overlay);
        Owner = owner;
        Placement = PopupPlacement.Below;
        _overlay = overlay;
        var resolvedTheme = theme ?? UITheme.Dark;
        _label = new Label(text)
        {
            TextStyle = resolvedTheme.GetTextStyle(UITextRole.Caption),
            IsHitTestVisible = false
        };
        AddChild(_label);
        IsHitTestVisible = false;
        IsVisible = false;
        owner.MouseEnter += OnOwnerEntered;
        owner.MouseLeave += OnOwnerLeft;
        _overlay.Add(this, Vector2.Zero);
        ResizeToText();
    }

    /// <inheritdoc/>
    protected override bool UpdateElement(double deltaTime)
    {
        if (!_pending || IsOpen || deltaTime <= 0d)
            return false;
        _elapsed += deltaTime;
        if (_elapsed < Math.Max(0d, Delay))
            return false;
        _pending = false;
        if (Owner is not { } owner || !owner.IsHovered)
            return false;
        PlacementOffset = Offset;
        Open();
        _overlay.PlacePopup(this);
        return true;
    }

    /// <inheritdoc/>
    protected override bool IsTimeUpdateActive => _pending && !IsOpen;

    /// <summary>Detaches owner events and removes the tooltip from its overlay.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Owner is { } owner)
        {
            owner.MouseEnter -= OnOwnerEntered;
            owner.MouseLeave -= OnOwnerLeft;
        }
        _overlay.Remove(this);
        GC.SuppressFinalize(this);
    }

    /// <summary>Starts the delayed-open timer.</summary>
    private void OnOwnerEntered()
    {
        _pending = true;
        _elapsed = 0d;
    }

    /// <summary>Cancels delayed opening and closes an open tooltip.</summary>
    private void OnOwnerLeft()
    {
        _pending = false;
        _elapsed = 0d;
        Close();
    }

    /// <summary>Sizes the popup around its current label text.</summary>
    private void ResizeToText()
    {
        Width = Label.MeasureTextWidth(_label.Text, _label.FontSize) + 12f;
        Height = _label.FontSize + 10f;
        _label.Width = MathF.Max(0f, Width - 12f);
        _label.Height = MathF.Max(0f, Height - 10f);
        Padding = new Thickness(6f, 5f);
    }
}
