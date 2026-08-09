using Engine.Graphics;
using Engine.Scripting;
using Engine.UI;

namespace ExampleGame;

/// <summary>Builds the example game's retained screen-space HUD.</summary>
public sealed class ExampleHud : SceneScript
{
    private const double FpsSampleIntervalSeconds = 0.5d;
    private Label? _fpsLabel;
    private double _fpsElapsedSeconds;
    private int _fpsFrameCount;
    private int _displayedFps = -1;

    /// <inheritdoc/>
    public override void OnReady()
    {
        if (Owner is not HudRoot hud)
            throw new InvalidOperationException("ExampleHud must be attached to a HUD root.");

        var theme = UITheme.Dark;
        var status = new Label("Third-person demo", 180f, 32f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16f),
            PaddingLeft = 10f,
            BackgroundColor = Color.FromSrgb(0x12, 0x13, 0x14),
            ForegroundColor = theme.TextPrimary,
            IsHitTestVisible = false,
            CornerRadius = 6f
        };
        _fpsLabel = new Label("FPS: --", 100f, 28f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16f, 56f, 0f, 0f),
            PaddingLeft = 10f,
            BackgroundColor = Color.FromSrgb(0x12, 0x13, 0x14),
            ForegroundColor = theme.TextPrimary,
            IsHitTestVisible = false,
            CornerRadius = 6f
        };
        var crosshair = new Label("+", 24f, 24f)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            PaddingLeft = 6f,
            FontSize = 20f,
            ForegroundColor = Color.White,
            IsHitTestVisible = false
        };
        var action = new Button(120f, 32f, "HUD button", theme, ButtonStyle.Primary)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16f)
        };
        action.Click += () => status.Text = "HUD button clicked";
        var root = UI.Overlay([status, _fpsLabel, crosshair, action]);
        root.Name = "ExampleHudContent";
        root.IsHitTestVisible = false;
        hud.Content = root;
    }

    /// <inheritdoc/>
    public override void OnUpdate(double deltaTime)
    {
        _fpsElapsedSeconds += Math.Max(0d, deltaTime);
        _fpsFrameCount++;
        if (_fpsElapsedSeconds < FpsSampleIntervalSeconds)
            return;

        var framesPerSecond = (int)Math.Round(_fpsFrameCount / _fpsElapsedSeconds);
        if (_fpsLabel is not null && framesPerSecond != _displayedFps)
        {
            _displayedFps = framesPerSecond;
            _fpsLabel.Text = $"FPS: {framesPerSecond}";
        }
        _fpsElapsedSeconds = 0d;
        _fpsFrameCount = 0;
    }
}
