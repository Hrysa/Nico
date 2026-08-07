using Engine.Graphics;

namespace Engine.UI;

/// <summary>A button that invokes immediately and repeatedly while held.</summary>
public sealed class RepeatButton : Button
{
    private bool _isRepeating;
    private double _elapsed;
    private double _nextInvocation;

    /// <summary>Gets or sets the initial hold duration before repetition, in seconds.</summary>
    public double Delay { get; set; } = 0.4;

    /// <summary>Gets or sets the interval between repeated invocations, in seconds.</summary>
    public double Interval { get; set; } = 0.06;

    /// <summary>Creates a themed repeat button.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="label">Button label.</param>
    /// <param name="theme">Theme supplying visual states.</param>
    public RepeatButton(float width, float height, string label, UITheme? theme = null)
        : base(width, height, label, theme ?? UITheme.Dark)
    {
        Pointer += OnPointer;
    }

    /// <inheritdoc/>
    protected override bool UpdateElement(double deltaTime)
    {
        if (!_isRepeating || deltaTime <= 0d)
            return false;
        _elapsed += deltaTime;
        var invoked = false;
        var interval = Math.Max(0.001, Interval);
        while (_elapsed >= _nextInvocation)
        {
            base.OnClick();
            _nextInvocation += interval;
            invoked = true;
        }
        return invoked;
    }

    /// <inheritdoc/>
    protected override bool IsTimeUpdateActive => _isRepeating;

    /// <summary>Starts or stops captured repetition.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer data.</param>
    private void OnPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase != UIRoutePhase.Target)
            return;
        if (pointerEvent.Kind == UIPointerEventKind.Press &&
            pointerEvent.Button == InputPointerButton.Primary)
        {
            _isRepeating = true;
            _elapsed = 0d;
            _nextInvocation = Math.Max(0d, Delay);
            SetPressed(true);
            pointerEvent.CapturePointer();
            pointerEvent.Handled = true;
            base.OnClick();
        }
        else if (pointerEvent.Kind == UIPointerEventKind.Release && _isRepeating)
        {
            _isRepeating = false;
            SetPressed(false);
            pointerEvent.ReleasePointerCapture();
            pointerEvent.Handled = true;
        }
    }
}
