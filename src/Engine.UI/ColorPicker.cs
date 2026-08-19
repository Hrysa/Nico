using System.Globalization;
using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Edits a linear RGBA color through an HSV popup, alpha strip, and hexadecimal input.</summary>
public sealed class ColorPicker : UIElement
{
    private const float PopupWidth = 220f;
    private const float PopupPadding = 10f;
    private const float PlaneHeight = 130f;
    private const float StripHeight = 16f;
    private const float RowGap = 8f;
    private readonly UITheme _theme;
    private readonly Button _header;
    private readonly ColorPickerHeader _headerContent;
    private readonly Popup _popup;
    private readonly SaturationValueSurface _saturationValue;
    private readonly ColorChannelStrip _hueStrip;
    private readonly ColorChannelStrip _alphaStrip;
    private readonly TextField _hexField;
    private Vector4 _value = Vector4.One;
    private float _hue;
    private float _saturation;
    private float _brightness = 1f;
    private bool _updatingChildren;

    /// <summary>Gets whether the picker exposes and formats an alpha channel.</summary>
    public bool ShowAlpha { get; }

    /// <summary>Gets whether the color popup is open.</summary>
    public bool IsDropDownOpen => _popup.IsOpen;

    /// <summary>Gets or sets the selected linear RGBA color.</summary>
    public Vector4 Value
    {
        get => _value;
        set => SetValue(value, notify: true);
    }

    /// <summary>Gets the selected color formatted as display-referred hexadecimal text.</summary>
    public string HexValue => FormatHex(_value, ShowAlpha);

    /// <summary>Occurs when the selected color changes.</summary>
    public event Action<Vector4>? ValueChanged;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Button,
        Name,
        HexValue,
        IsEnabled,
        false,
        false,
        null,
        Actions: UISemanticAction.Invoke | UISemanticAction.ExpandCollapse,
        IsExpanded: IsDropDownOpen);

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (!IsEnabled || action is not (UISemanticAction.Invoke or
                UISemanticAction.ExpandCollapse))
            return false;
        TogglePopup();
        return true;
    }

    /// <summary>Creates a compact picker with an owned floating editing surface.</summary>
    /// <param name="width">Collapsed control width.</param>
    /// <param name="height">Collapsed control height.</param>
    /// <param name="showAlpha">Whether alpha is editable and included in hexadecimal text.</param>
    /// <param name="theme">Theme supplying control surfaces and typography.</param>
    public ColorPicker(
        float width,
        float height,
        bool showAlpha = true,
        UITheme? theme = null)
        : base(width, height)
    {
        ShowAlpha = showAlpha;
        _theme = theme ?? UITheme.Dark;
        _headerContent = new ColorPickerHeader(width, height, _theme)
        {
            IsHitTestVisible = false
        };
        _header = new Button(width, height, _theme)
        {
            Name = "ColorPickerHeader",
            Content = _headerContent
        };
        _header.Click += TogglePopup;
        _popup = new Popup(_theme.SurfaceRaised, _theme.BorderStrong,
            PopupWidth, ResolvePopupHeight())
        {
            Name = "ColorPickerPopup",
            Owner = _header,
            IsVisible = false,
            Placement = PopupPlacement.Below,
            ConstraintMargin = 4f
        };
        var contentWidth = PopupWidth - PopupPadding * 2f;
        _saturationValue = new SaturationValueSurface(contentWidth, PlaneHeight, _theme)
        {
            Name = "ColorPickerSaturationValue"
        };
        _saturationValue.ValueChanged += OnSaturationValueChanged;
        _hueStrip = new ColorChannelStrip(
            ColorChannelStripKind.Hue, contentWidth, StripHeight, _theme)
        {
            Name = "ColorPickerHue"
        };
        _hueStrip.ValueChanged += OnHueChanged;
        _alphaStrip = new ColorChannelStrip(
            ColorChannelStripKind.Alpha, contentWidth, StripHeight, _theme)
        {
            Name = "ColorPickerAlpha",
            IsVisible = ShowAlpha
        };
        _alphaStrip.ValueChanged += OnAlphaChanged;
        _hexField = new TextField(contentWidth, 30f, _theme)
        {
            Name = "ColorPickerHex",
            Placeholder = ShowAlpha ? "#RRGGBBAA" : "#RRGGBB",
            UpdateTrigger = TextUpdateTrigger.Commit,
            Validator = ValidateHex
        };
        _hexField.ValueUpdateRequested += OnHexRequested;
        _popup.AddChild(_saturationValue);
        _popup.AddChild(_hueStrip);
        _popup.AddChild(_alphaStrip);
        _popup.AddChild(_hexField);
        AddChild(_header);
        AddChild(_popup);
        SetValue(Vector4.One, notify: false);
    }

    /// <summary>Updates the selected color without raising <see cref="ValueChanged"/>.</summary>
    /// <param name="value">Linear RGBA value to display.</param>
    public void SetValueWithoutNotification(Vector4 value)
    {
        SetValue(value, notify: false);
    }

    /// <summary>Parses and selects conventional hexadecimal RGB or RGBA text.</summary>
    /// <param name="text">Text in RGB, RGBA, RRGGBB, or RRGGBBAA form, with optional '#'.</param>
    /// <returns>True when the text was valid and selected.</returns>
    public bool TrySetHex(string text)
    {
        if (!TryParseHex(text, ShowAlpha, out var parsed))
            return false;
        SetValue(parsed, notify: true);
        return true;
    }

    /// <summary>Opens the owned color-editing popup.</summary>
    public void Open()
    {
        SynchronizeChildren();
        _popup.Open();
        InvalidateMeasure();
    }

    /// <summary>Closes the owned color-editing popup.</summary>
    public void Close()
    {
        _popup.Close();
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _header.Measure(new Vector2(availableSize.X, Height));
        _popup.Measure(new Vector2(PopupWidth, ResolvePopupHeight()));
        return new Vector2(availableSize.X, Height);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        _header.Arrange(Vector2.Zero, contentSize);
        var popupHeight = ResolvePopupHeight();
        _popup.Arrange(new Vector2(0f, contentSize.Y), new Vector2(PopupWidth, popupHeight));
        var contentWidth = PopupWidth - PopupPadding * 2f;
        var y = PopupPadding;
        _saturationValue.Arrange(new Vector2(PopupPadding, y),
            new Vector2(contentWidth, PlaneHeight));
        y += PlaneHeight + RowGap;
        _hueStrip.Arrange(new Vector2(PopupPadding, y),
            new Vector2(contentWidth, StripHeight));
        y += StripHeight + RowGap;
        if (ShowAlpha)
        {
            _alphaStrip.Arrange(new Vector2(PopupPadding, y),
                new Vector2(contentWidth, StripHeight));
            y += StripHeight + RowGap;
        }
        _hexField.Arrange(new Vector2(PopupPadding, y), new Vector2(contentWidth, 30f));
    }

    /// <summary>Opens or closes the editing popup.</summary>
    private void TogglePopup()
    {
        if (_popup.IsOpen)
            Close();
        else
            Open();
    }

    /// <summary>Clamps, decomposes, redraws, and optionally publishes one selected value.</summary>
    /// <param name="value">Requested linear RGBA value.</param>
    /// <param name="notify">Whether observers receive a change notification.</param>
    private void SetValue(Vector4 value, bool notify)
    {
        if (!IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        var resolved = Vector4.Clamp(value, Vector4.Zero, Vector4.One);
        if (!ShowAlpha)
            resolved.W = 1f;
        if (_value == resolved)
        {
            SynchronizeChildren();
            return;
        }
        _value = resolved;
        LinearRgbToHsv(new Vector3(resolved.X, resolved.Y, resolved.Z),
            out _hue, out _saturation, out _brightness);
        SynchronizeChildren();
        InvalidateVisual();
        if (notify)
            ValueChanged?.Invoke(_value);
    }

    /// <summary>Refreshes every retained visual from the authoritative selected value.</summary>
    private void SynchronizeChildren()
    {
        _updatingChildren = true;
        try
        {
            _headerContent.Update(_value, ShowAlpha);
            _saturationValue.SetSelection(_hue, _saturation, _brightness);
            _hueStrip.SetDisplay(_hue, _value);
            _alphaStrip.SetDisplay(_value.W, _value);
            if (!_hexField.IsFocused)
                _hexField.Text = HexValue;
        }
        finally
        {
            _updatingChildren = false;
        }
    }

    /// <summary>Applies a saturation/value plane interaction.</summary>
    /// <param name="saturation">Selected saturation.</param>
    /// <param name="brightness">Selected HSV value.</param>
    private void OnSaturationValueChanged(float saturation, float brightness)
    {
        if (_updatingChildren)
            return;
        _saturation = saturation;
        _brightness = brightness;
        ApplyHsvSelection();
    }

    /// <summary>Applies a hue-strip interaction.</summary>
    /// <param name="hue">Selected normalized hue.</param>
    private void OnHueChanged(float hue)
    {
        if (_updatingChildren)
            return;
        _hue = hue;
        ApplyHsvSelection();
    }

    /// <summary>Applies an alpha-strip interaction.</summary>
    /// <param name="alpha">Selected normalized opacity.</param>
    private void OnAlphaChanged(float alpha)
    {
        if (_updatingChildren)
            return;
        SetValue(_value with { W = alpha }, notify: true);
    }

    /// <summary>Converts current HSV editing state back into the authoritative linear color.</summary>
    private void ApplyHsvSelection()
    {
        var hue = _hue;
        var saturation = _saturation;
        var brightness = _brightness;
        var rgb = HsvToLinearRgb(_hue, _saturation, _brightness);
        SetValue(new Vector4(rgb, _value.W), notify: true);
        _hue = hue;
        _saturation = saturation;
        _brightness = brightness;
        SynchronizeChildren();
    }

    /// <summary>Commits hexadecimal input or restores canonical text after rejection.</summary>
    /// <param name="text">Requested hexadecimal text.</param>
    private void OnHexRequested(string text)
    {
        if (!TrySetHex(text))
            _hexField.Text = HexValue;
    }

    /// <summary>Validates hexadecimal text for the configured alpha mode.</summary>
    /// <param name="text">Candidate text.</param>
    /// <returns>Error text, or null when parseable.</returns>
    private string? ValidateHex(string text) => TryParseHex(text, ShowAlpha, out _)
        ? null
        : ShowAlpha ? "Enter #RGB, #RGBA, #RRGGBB, or #RRGGBBAA."
            : "Enter #RGB or #RRGGBB.";

    /// <summary>Gets the popup height required by the configured channel rows.</summary>
    /// <returns>Popup height in logical pixels.</returns>
    private float ResolvePopupHeight() => ShowAlpha ? 242f : 218f;

    /// <summary>Formats a linear color as conventional display-referred hexadecimal text.</summary>
    /// <param name="value">Linear RGBA value.</param>
    /// <param name="includeAlpha">Whether to append an alpha byte.</param>
    /// <returns>Canonical uppercase hexadecimal text.</returns>
    public static string FormatHex(Vector4 value, bool includeAlpha)
    {
        if (!IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        var red = ToSrgbByte(value.X);
        var green = ToSrgbByte(value.Y);
        var blue = ToSrgbByte(value.Z);
        var alpha = (byte)Math.Clamp((int)MathF.Round(value.W * 255f), 0, 255);
        return includeAlpha
            ? $"#{red:X2}{green:X2}{blue:X2}{alpha:X2}"
            : $"#{red:X2}{green:X2}{blue:X2}";
    }

    /// <summary>Parses supported conventional hexadecimal forms into linear RGBA.</summary>
    /// <param name="text">Candidate hexadecimal text.</param>
    /// <param name="allowAlpha">Whether alpha-bearing forms are accepted.</param>
    /// <param name="value">Parsed linear RGBA value.</param>
    /// <returns>True when parsing succeeded.</returns>
    public static bool TryParseHex(string text, bool allowAlpha, out Vector4 value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var span = text.AsSpan().Trim();
        if (!span.IsEmpty && span[0] == '#')
            span = span[1..];
        if (span.Length is 3 or 4)
        {
            if (span.Length == 4 && !allowAlpha ||
                !TryHexNibble(span[0], out var r) || !TryHexNibble(span[1], out var g) ||
                !TryHexNibble(span[2], out var b) ||
                span.Length == 4 && !TryHexNibble(span[3], out _))
                return false;
            var alpha = span.Length == 4 ? ExpandNibble(span[3]) : byte.MaxValue;
            value = new Vector4(ToLinear(ExpandNibble(span[0])),
                ToLinear(ExpandNibble(span[1])), ToLinear(ExpandNibble(span[2])),
                alpha / 255f);
            return true;
        }
        if (span.Length is not (6 or 8) || span.Length == 8 && !allowAlpha)
            return false;
        if (!byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out var red) ||
            !byte.TryParse(span.Slice(2, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(span.Slice(4, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var blue) ||
            span.Length == 8 && !byte.TryParse(span.Slice(6, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out _))
            return false;
        var parsedAlpha = span.Length == 8
            ? byte.Parse(span.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : byte.MaxValue;
        value = new Vector4(ToLinear(red), ToLinear(green), ToLinear(blue),
            parsedAlpha / 255f);
        return true;
    }

    /// <summary>Converts a linear RGB value into normalized HSV editing components.</summary>
    /// <param name="linearRgb">Linear RGB value.</param>
    /// <param name="hue">Normalized hue.</param>
    /// <param name="saturation">Normalized saturation.</param>
    /// <param name="brightness">Normalized HSV value.</param>
    private static void LinearRgbToHsv(
        Vector3 linearRgb,
        out float hue,
        out float saturation,
        out float brightness)
    {
        var rgb = new Vector3(ToSrgb(linearRgb.X), ToSrgb(linearRgb.Y), ToSrgb(linearRgb.Z));
        var maximum = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        var minimum = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        var delta = maximum - minimum;
        brightness = maximum;
        saturation = maximum <= float.Epsilon ? 0f : delta / maximum;
        if (delta <= float.Epsilon)
        {
            hue = 0f;
            return;
        }
        var sector = maximum == rgb.X
            ? (rgb.Y - rgb.Z) / delta % 6f
            : maximum == rgb.Y
                ? (rgb.Z - rgb.X) / delta + 2f
                : (rgb.X - rgb.Y) / delta + 4f;
        hue = sector / 6f;
        if (hue < 0f)
            hue += 1f;
    }

    /// <summary>Converts normalized HSV components into a linear RGB value.</summary>
    /// <param name="hue">Normalized hue.</param>
    /// <param name="saturation">Normalized saturation.</param>
    /// <param name="brightness">Normalized HSV value.</param>
    /// <returns>Linear RGB value.</returns>
    internal static Vector3 HsvToLinearRgb(float hue, float saturation, float brightness)
    {
        hue = hue - MathF.Floor(hue);
        saturation = Math.Clamp(saturation, 0f, 1f);
        brightness = Math.Clamp(brightness, 0f, 1f);
        var chroma = brightness * saturation;
        var sector = hue * 6f;
        var secondary = chroma * (1f - MathF.Abs(sector % 2f - 1f));
        var srgb = sector switch
        {
            < 1f => new Vector3(chroma, secondary, 0f),
            < 2f => new Vector3(secondary, chroma, 0f),
            < 3f => new Vector3(0f, chroma, secondary),
            < 4f => new Vector3(0f, secondary, chroma),
            < 5f => new Vector3(secondary, 0f, chroma),
            _ => new Vector3(chroma, 0f, secondary)
        };
        var match = brightness - chroma;
        srgb += new Vector3(match);
        return new Vector3(ToLinear(srgb.X), ToLinear(srgb.Y), ToLinear(srgb.Z));
    }

    /// <summary>Converts one linear component into a display-referred byte.</summary>
    /// <param name="linear">Linear component.</param>
    /// <returns>Nearest sRGB byte.</returns>
    private static byte ToSrgbByte(float linear) =>
        (byte)Math.Clamp((int)MathF.Round(ToSrgb(linear) * 255f), 0, 255);

    /// <summary>Converts one normalized linear component into normalized sRGB.</summary>
    /// <param name="linear">Linear component.</param>
    /// <returns>Normalized sRGB component.</returns>
    private static float ToSrgb(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        return linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Converts one normalized sRGB component into normalized linear light.</summary>
    /// <param name="srgb">sRGB component.</param>
    /// <returns>Linear component.</returns>
    private static float ToLinear(float srgb)
    {
        srgb = Math.Clamp(srgb, 0f, 1f);
        return srgb <= 0.04045f
            ? srgb / 12.92f
            : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>Converts one sRGB byte into normalized linear light.</summary>
    /// <param name="value">sRGB byte.</param>
    /// <returns>Linear component.</returns>
    private static float ToLinear(byte value) => ToLinear(value / 255f);

    /// <summary>Parses one hexadecimal character.</summary>
    /// <param name="character">Candidate digit.</param>
    /// <param name="value">Parsed nibble.</param>
    /// <returns>True when the character was hexadecimal.</returns>
    private static bool TryHexNibble(char character, out byte value)
    {
        if (character is >= '0' and <= '9')
            value = (byte)(character - '0');
        else if (character is >= 'A' and <= 'F')
            value = (byte)(character - 'A' + 10);
        else if (character is >= 'a' and <= 'f')
            value = (byte)(character - 'a' + 10);
        else
        {
            value = 0;
            return false;
        }
        return true;
    }

    /// <summary>Expands a hexadecimal nibble into its repeated byte representation.</summary>
    /// <param name="character">Known hexadecimal character.</param>
    /// <returns>Expanded byte.</returns>
    private static byte ExpandNibble(char character)
    {
        TryHexNibble(character, out var value);
        return (byte)(value * 17);
    }

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    /// <summary>Paints the collapsed checkerboard swatch and hexadecimal label.</summary>
    private sealed class ColorPickerHeader : UIElement
    {
        private readonly UITheme _theme;
        private readonly Label _label;
        private Vector4 _value = Vector4.One;

        /// <summary>Creates collapsed picker content.</summary>
        /// <param name="width">Initial content width.</param>
        /// <param name="height">Initial content height.</param>
        /// <param name="theme">Theme supplying label color.</param>
        internal ColorPickerHeader(float width, float height, UITheme theme) : base(width, height)
        {
            _theme = theme;
            _label = new Label(string.Empty)
            {
                TextStyle = theme.GetTextStyle(UITextRole.Body),
                IsHitTestVisible = false
            };
            AddChild(_label);
        }

        /// <summary>Updates swatch and formatted text.</summary>
        /// <param name="value">Linear RGBA value.</param>
        /// <param name="showAlpha">Whether alpha appears in text.</param>
        internal void Update(Vector4 value, bool showAlpha)
        {
            _value = value;
            _label.Text = FormatHex(value, showAlpha);
            InvalidateVisual();
        }

        /// <inheritdoc/>
        protected override void ArrangeOverride(Vector2 contentSize)
        {
            _label.Arrange(new Vector2(34f, 0f),
                new Vector2(MathF.Max(0f, contentSize.X - 34f), contentSize.Y));
        }

        /// <inheritdoc/>
        protected override void Paint(UIDrawList drawList)
        {
            var top = Top + 5f;
            var bottom = Bottom - 5f;
            PaintChecker(drawList, Left, top, Left + 24f, bottom, _theme);
            PaintComposite(drawList, Left, top, Left + 24f, bottom, _value, _theme);
            drawList.AddRectangle(Left, top, Left + 24f, top + 1f, _theme.BorderStrong);
            drawList.AddRectangle(Left, bottom - 1f, Left + 24f, bottom, _theme.BorderStrong);
            drawList.AddRectangle(Left, top + 1f, Left + 1f, bottom - 1f, _theme.BorderStrong);
            drawList.AddRectangle(Left + 23f, top + 1f, Left + 24f, bottom - 1f, _theme.BorderStrong);
        }
    }

    /// <summary>Provides pointer-captured normalized selection for picker surfaces.</summary>
    private abstract class ColorDragSurface : UIElement
    {
        /// <summary>Creates a fixed-size drag surface.</summary>
        /// <param name="width">Surface width.</param>
        /// <param name="height">Surface height.</param>
        protected ColorDragSurface(float width, float height) : base(width, height)
        {
            var gesture = new PointerCaptureGesture(this, handleMoves: true);
            gesture.PositionChanged += Select;
        }

        /// <summary>Applies one local pointer position to the selected channel.</summary>
        /// <param name="position">Surface-local pointer position.</param>
        protected abstract void Select(Vector2 position);

    }

    /// <summary>Paints and edits the two-dimensional saturation/value plane.</summary>
    private sealed class SaturationValueSurface : ColorDragSurface
    {
        private const int Columns = 16;
        private const int Rows = 10;
        private readonly UITheme _theme;
        private float _hue;
        private float _saturation;
        private float _brightness = 1f;

        /// <summary>Occurs when saturation or brightness changes.</summary>
        internal event Action<float, float>? ValueChanged;

        /// <summary>Creates one saturation/value surface.</summary>
        /// <param name="width">Surface width.</param>
        /// <param name="height">Surface height.</param>
        /// <param name="theme">Theme supplying marker colors.</param>
        internal SaturationValueSurface(float width, float height, UITheme theme)
            : base(width, height)
        {
            _theme = theme;
            ClipToBounds = true;
        }

        /// <summary>Synchronizes displayed HSV selection.</summary>
        /// <param name="hue">Normalized hue.</param>
        /// <param name="saturation">Normalized saturation.</param>
        /// <param name="brightness">Normalized value.</param>
        internal void SetSelection(float hue, float saturation, float brightness)
        {
            _hue = hue;
            _saturation = saturation;
            _brightness = brightness;
            InvalidateVisual();
        }

        /// <inheritdoc/>
        protected override void Select(Vector2 position)
        {
            _saturation = Math.Clamp(position.X / MathF.Max(1f, Width), 0f, 1f);
            _brightness = 1f - Math.Clamp(position.Y / MathF.Max(1f, Height), 0f, 1f);
            InvalidateVisual();
            ValueChanged?.Invoke(_saturation, _brightness);
        }

        /// <inheritdoc/>
        protected override void Paint(UIDrawList drawList)
        {
            var cellWidth = Width / Columns;
            var cellHeight = Height / Rows;
            for (var row = 0; row < Rows; row++)
            {
                var brightness = 1f - (row + 0.5f) / Rows;
                for (var column = 0; column < Columns; column++)
                {
                    var saturation = (column + 0.5f) / Columns;
                    drawList.AddRectangle(
                        Left + column * cellWidth,
                        Top + row * cellHeight,
                        Left + (column + 1) * cellWidth + 0.5f,
                        Top + (row + 1) * cellHeight + 0.5f,
                        new Color(HsvToLinearRgb(_hue, saturation, brightness)));
                }
            }
            var x = Left + _saturation * Width;
            var y = Top + (1f - _brightness) * Height;
            drawList.AddEllipseStroke(x - 5f, y - 5f, x + 5f, y + 5f,
                2f, _theme.Canvas);
            drawList.AddEllipseStroke(x - 3f, y - 3f, x + 3f, y + 3f,
                1f, Color.White);
        }
    }

    /// <summary>Identifies the normalized channel edited by a horizontal color strip.</summary>
    private enum ColorChannelStripKind
    {
        /// <summary>HSV hue.</summary>
        Hue,
        /// <summary>Linear alpha.</summary>
        Alpha
    }

    /// <summary>Paints and edits a horizontal hue or alpha strip.</summary>
    private sealed class ColorChannelStrip : ColorDragSurface
    {
        private const int Segments = 18;
        private readonly ColorChannelStripKind _kind;
        private readonly UITheme _theme;
        private float _selection;
        private Vector4 _color = Vector4.One;

        /// <summary>Occurs when the normalized channel changes.</summary>
        internal event Action<float>? ValueChanged;

        /// <summary>Creates a horizontal color channel strip.</summary>
        /// <param name="kind">Channel represented by this strip.</param>
        /// <param name="width">Strip width.</param>
        /// <param name="height">Strip height.</param>
        /// <param name="theme">Theme supplying marker and checker colors.</param>
        internal ColorChannelStrip(
            ColorChannelStripKind kind,
            float width,
            float height,
            UITheme theme)
            : base(width, height)
        {
            _kind = kind;
            _theme = theme;
            ClipToBounds = true;
        }

        /// <summary>Synchronizes selected channel and current composite color.</summary>
        /// <param name="selection">Normalized channel selection.</param>
        /// <param name="color">Current linear RGBA color.</param>
        internal void SetDisplay(float selection, Vector4 color)
        {
            _selection = selection;
            _color = color;
            InvalidateVisual();
        }

        /// <inheritdoc/>
        protected override void Select(Vector2 position)
        {
            _selection = Math.Clamp(position.X / MathF.Max(1f, Width), 0f, 1f);
            InvalidateVisual();
            ValueChanged?.Invoke(_selection);
        }

        /// <inheritdoc/>
        protected override void Paint(UIDrawList drawList)
        {
            var segmentWidth = Width / Segments;
            for (var segment = 0; segment < Segments; segment++)
            {
                var amount = (segment + 0.5f) / Segments;
                var left = Left + segment * segmentWidth;
                var right = Left + (segment + 1) * segmentWidth + 0.5f;
                if (_kind == ColorChannelStripKind.Hue)
                {
                    drawList.AddRectangle(left, Top, right, Bottom,
                        new Color(HsvToLinearRgb(amount, 1f, 1f)));
                }
                else
                {
                    PaintChecker(drawList, left, Top, right, Bottom, _theme);
                    PaintComposite(drawList, left, Top, right, Bottom,
                        _color with { W = amount }, _theme);
                }
            }
            var x = Left + _selection * Width;
            drawList.AddRectangle(x - 2f, Top, x + 2f, Bottom, _theme.Canvas);
            drawList.AddRectangle(x - 1f, Top + 1f, x + 1f, Bottom - 1f, Color.White);
        }
    }

    /// <summary>Paints a small checkerboard into arbitrary bounds.</summary>
    /// <param name="drawList">Target draw list.</param>
    /// <param name="left">Left bound.</param>
    /// <param name="top">Top bound.</param>
    /// <param name="right">Right bound.</param>
    /// <param name="bottom">Bottom bound.</param>
    /// <param name="theme">Theme supplying neutral colors.</param>
    private static void PaintChecker(
        UIDrawList drawList,
        float left,
        float top,
        float right,
        float bottom,
        UITheme theme)
    {
        var middleX = (left + right) * 0.5f;
        var middleY = (top + bottom) * 0.5f;
        drawList.AddRectangle(left, top, middleX, middleY, theme.SurfacePressed);
        drawList.AddRectangle(middleX, top, right, middleY, theme.BorderStrong);
        drawList.AddRectangle(left, middleY, middleX, bottom, theme.BorderStrong);
        drawList.AddRectangle(middleX, middleY, right, bottom, theme.SurfacePressed);
    }

    /// <summary>Paints an alpha-composited color over the checker's two neutral tones.</summary>
    /// <param name="drawList">Target draw list.</param>
    /// <param name="left">Left bound.</param>
    /// <param name="top">Top bound.</param>
    /// <param name="right">Right bound.</param>
    /// <param name="bottom">Bottom bound.</param>
    /// <param name="value">Linear RGBA value.</param>
    /// <param name="theme">Theme supplying checker tones.</param>
    private static void PaintComposite(
        UIDrawList drawList,
        float left,
        float top,
        float right,
        float bottom,
        Vector4 value,
        UITheme theme)
    {
        var color = new Color(value.X, value.Y, value.Z);
        var first = Color.Lerp(theme.SurfacePressed, color, value.W);
        var second = Color.Lerp(theme.BorderStrong, color, value.W);
        var middleX = (left + right) * 0.5f;
        var middleY = (top + bottom) * 0.5f;
        drawList.AddRectangle(left, top, middleX, middleY, first);
        drawList.AddRectangle(middleX, top, right, middleY, second);
        drawList.AddRectangle(left, middleY, middleX, bottom, second);
        drawList.AddRectangle(middleX, middleY, right, bottom, first);
    }
}
