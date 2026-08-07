using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Edits a bounded scalar value with pointer drag, track press, and keyboard input.</summary>
public sealed class Slider : UIElement
{
    private const float DefaultThumbSize = 14f;
    private readonly UITheme _theme;
    private float _minimum;
    private float _maximum = 1f;
    private float _value;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Slider,
        Name,
        Value.ToString(Culture.NumberFormat),
        IsEnabled,
        false,
        false,
        null,
        Actions: UISemanticAction.Increment | UISemanticAction.Decrement | UISemanticAction.SetValue,
        NumericValue: Value,
        Minimum: Minimum,
        Maximum: Maximum);

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (!IsEnabled)
            return false;
        if (action == UISemanticAction.Increment)
            Value += SmallChange;
        else if (action == UISemanticAction.Decrement)
            Value -= SmallChange;
        else if (action == UISemanticAction.SetValue && value is double requested
            && double.IsFinite(requested))
            Value = (float)requested;
        else
            return false;
        return true;
    }

    /// <summary>Gets the slider orientation.</summary>
    public UIOrientation SliderOrientation { get; }

    /// <summary>Gets the draggable slider thumb.</summary>
    public Thumb Thumb { get; }

    /// <summary>Gets or sets the minimum value.</summary>
    public float Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum == value)
                return;
            _minimum = value;
            if (_maximum < _minimum)
                _maximum = _minimum;
            SetValue(_value);
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the maximum value.</summary>
    public float Maximum
    {
        get => _maximum;
        set
        {
            if (_maximum == value)
                return;
            _maximum = MathF.Max(Minimum, value);
            SetValue(_value);
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the current clamped value.</summary>
    public float Value
    {
        get => _value;
        set => SetValue(value);
    }

    /// <summary>Gets or sets the keyboard increment.</summary>
    public float SmallChange { get; set; } = 0.1f;

    /// <summary>Occurs when the current value changes.</summary>
    public event Action<float>? ValueChanged;

    /// <summary>Creates a slider.</summary>
    /// <param name="orientation">Slider axis.</param>
    /// <param name="width">Slider width.</param>
    /// <param name="height">Slider height.</param>
    /// <param name="theme">Theme supplying track and thumb colors.</param>
    public Slider(
        UIOrientation orientation,
        float width,
        float height,
        UITheme? theme = null)
        : base(width, height)
    {
        SliderOrientation = orientation;
        _theme = theme ?? UITheme.Dark;
        IsTabStop = true;
        Thumb = new Thumb(_theme);
        Thumb.DragDelta += OnThumbDragDelta;
        AddChild(Thumb);
        Pointer += OnPointer;
        Key += OnKey;
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        Thumb.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var ratio = ResolveRatio();
        if (SliderOrientation == UIOrientation.Horizontal)
        {
            var size = MathF.Min(DefaultThumbSize, contentSize.X);
            Thumb.Arrange(new Vector2((contentSize.X - size) * ratio, 0f),
                new Vector2(size, contentSize.Y));
        }
        else
        {
            var size = MathF.Min(DefaultThumbSize, contentSize.Y);
            Thumb.Arrange(new Vector2(0f, (contentSize.Y - size) * (1f - ratio)),
                new Vector2(contentSize.X, size));
        }
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (SliderOrientation == UIOrientation.Horizontal)
        {
            var y = Top + Height / 2f;
            drawList.AddLine(Left + DefaultThumbSize / 2f, y,
                Right - DefaultThumbSize / 2f, y, 2f, _theme.BorderStrong);
            drawList.AddLine(Left + DefaultThumbSize / 2f, y,
                Left + DefaultThumbSize / 2f +
                    MathF.Max(0f, Width - DefaultThumbSize) * ResolveRatio(),
                y, 2f, _theme.Accent);
        }
        else
        {
            var x = Left + Width / 2f;
            drawList.AddLine(x, Top + DefaultThumbSize / 2f,
                x, Bottom - DefaultThumbSize / 2f, 2f, _theme.BorderStrong);
            drawList.AddLine(x, Bottom - DefaultThumbSize / 2f,
                x, Bottom - DefaultThumbSize / 2f -
                    MathF.Max(0f, Height - DefaultThumbSize) * ResolveRatio(),
                2f, _theme.Accent);
        }
    }

    /// <summary>Maps track presses directly to a value.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer data.</param>
    private void OnPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase != UIRoutePhase.Target ||
            pointerEvent.Kind != UIPointerEventKind.Press)
            return;
        var ratio = SliderOrientation == UIOrientation.Horizontal
            ? pointerEvent.LocalPosition.X / MathF.Max(1f, Width)
            : 1f - pointerEvent.LocalPosition.Y / MathF.Max(1f, Height);
        SetValue(Minimum + Math.Clamp(ratio, 0f, 1f) * (Maximum - Minimum));
        pointerEvent.Handled = true;
    }

    /// <summary>Applies keyboard increments while the slider is focused.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="keyEvent">Routed key data.</param>
    private void OnKey(UIElement sender, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.RoutePhase != UIRoutePhase.Target || keyEvent.Kind != UIKeyEventKind.KeyDown)
            return;
        var direction = keyEvent.Key switch
        {
            InputKey.Left or InputKey.Down => -1f,
            InputKey.Right or InputKey.Up => 1f,
            InputKey.Home => float.NegativeInfinity,
            InputKey.End => float.PositiveInfinity,
            _ => 0f
        };
        if (direction == 0f)
            return;
        SetValue(float.IsNegativeInfinity(direction) ? Minimum :
            float.IsPositiveInfinity(direction) ? Maximum : Value + direction * SmallChange);
        keyEvent.Handled = true;
    }

    /// <summary>Converts captured thumb movement into value movement.</summary>
    /// <param name="delta">Logical drag movement.</param>
    private void OnThumbDragDelta(Vector2 delta)
    {
        var travel = SliderOrientation == UIOrientation.Horizontal
            ? MathF.Max(0f, Width - DefaultThumbSize)
            : MathF.Max(0f, Height - DefaultThumbSize);
        if (travel <= 0f)
            return;
        var movement = SliderOrientation == UIOrientation.Horizontal ? delta.X : -delta.Y;
        SetValue(Value + movement * (Maximum - Minimum) / travel);
    }

    /// <summary>Gets the normalized current value.</summary>
    /// <returns>A value in the inclusive zero-to-one range.</returns>
    private float ResolveRatio() => Maximum <= Minimum ? 0f : (Value - Minimum) / (Maximum - Minimum);

    /// <summary>Clamps, invalidates, and reports a value change.</summary>
    /// <param name="value">Requested value.</param>
    private void SetValue(float value)
    {
        var resolved = Math.Clamp(value, Minimum, Maximum);
        if (_value == resolved)
            return;
        _value = resolved;
        InvalidateArrange();
        InvalidateVisual();
        ValueChanged?.Invoke(_value);
    }
}
