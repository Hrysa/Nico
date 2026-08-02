using System.Numerics;
using Engine.UI;

namespace Editor;

/// <summary>
/// Displays modal indeterminate progress while game scripts are compiled.
/// </summary>
public sealed class CompilationProgressDialog : Modal
{
    private readonly Surface _indicator;
    private readonly float _trackLeft;
    private readonly float _travelWidth;
    private double _elapsedTime;

    /// <summary>
    /// Creates a centered script-compilation progress dialog.
    /// </summary>
    /// <param name="width">Editor window width.</param>
    /// <param name="height">Editor window height.</param>
    /// <param name="theme">Theme supplying dialog visuals.</param>
    public CompilationProgressDialog(float width, float height, UITheme? theme = null)
        : base(width, height, MathF.Min(420f, width - 48f), 156f, theme)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        Dialog.AddChild(new DialogHeader(0f, 0f, Dialog.Width,
            "Preparing Play Mode", "Compiling C# scripts...", resolvedTheme));

        _trackLeft = 20f;
        const float trackHeight = 6f;
        const float indicatorWidth = 84f;
        var trackWidth = MathF.Max(indicatorWidth, Dialog.Width - _trackLeft * 2f);
        var track = new Surface(_trackLeft, 112f, trackWidth, trackHeight,
            resolvedTheme.SurfacePressed, resolvedTheme.SurfacePressed)
        {
            Name = "CompilationProgressTrack"
        };
        _indicator = new Surface(0f, 0f, indicatorWidth, trackHeight,
            resolvedTheme.Accent, resolvedTheme.Accent)
        {
            Name = "CompilationProgressIndicator"
        };
        track.AddChild(_indicator);
        Dialog.AddChild(track);
        _travelWidth = MathF.Max(0f, trackWidth - indicatorWidth);
    }

    /// <summary>Advances the indeterminate progress animation.</summary>
    /// <param name="deltaTime">Elapsed time in seconds since the previous update.</param>
    public void Update(double deltaTime)
    {
        _elapsedTime += Math.Max(0d, deltaTime);
        var phase = (float)(_elapsedTime % 1.4d / 1.4d);
        var pingPong = phase <= 0.5f ? phase * 2f : (1f - phase) * 2f;
        _indicator.Position = new Vector3(MathF.Round(pingPong * _travelWidth), 0f, 0f);
    }
}
