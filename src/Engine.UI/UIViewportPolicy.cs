using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Describes logical UI coordinates resolved for a physical client viewport.</summary>
/// <param name="LogicalSize">Complete logical viewport size used by projection and input.</param>
/// <param name="ContentBounds">Safe logical rectangle allocated to the root element.</param>
/// <param name="Scale">Client units represented by one logical UI unit.</param>
public readonly record struct UIViewportLayout(
    Vector2 LogicalSize,
    UIClipRect ContentBounds,
    float Scale)
{
    /// <summary>Maps a client position into logical UI coordinates.</summary>
    /// <param name="physicalPosition">Position in logical client units.</param>
    /// <returns>Logical position used by layout and routed input.</returns>
    public Vector2 ToLogical(Vector2 physicalPosition) => physicalPosition / Scale;

    /// <summary>Maps a client delta into logical UI units.</summary>
    /// <param name="physicalDelta">Delta in logical client units.</param>
    /// <returns>Logical input delta.</returns>
    public Vector2 DeltaToLogical(Vector2 physicalDelta) => physicalDelta / Scale;
}

/// <summary>Resolves runtime UI layout and input coordinates for a client viewport.</summary>
public interface IUIViewportPolicy
{
    /// <summary>Resolves layout for the current client size and framebuffer density.</summary>
    /// <param name="clientSize">Size in logical client units.</param>
    /// <param name="rasterScale">Physical framebuffer pixels per client unit.</param>
    /// <returns>Logical viewport, safe content bounds, and scale.</returns>
    UIViewportLayout Resolve(Vector2 clientSize, float rasterScale = 1f);
}

/// <summary>Uses one logical UI unit per physical client pixel.</summary>
public sealed class StretchUIViewportPolicy : IUIViewportPolicy
{
    /// <summary>Gets the shared stateless policy.</summary>
    public static StretchUIViewportPolicy Instance { get; } = new();

    /// <summary>Prevents external construction of the stateless singleton.</summary>
    private StretchUIViewportPolicy()
    {
    }

    /// <inheritdoc/>
    public UIViewportLayout Resolve(Vector2 clientSize, float rasterScale = 1f)
    {
        var width = MathF.Max(1f, clientSize.X);
        var height = MathF.Max(1f, clientSize.Y);
        return new UIViewportLayout(
            new Vector2(width, height),
            new UIClipRect(0f, 0f, width, height),
            1f);
    }
}

/// <summary>
/// Preserves a reference-resolution scale while expanding logical space for different aspect ratios.
/// </summary>
public sealed class ReferenceResolutionUIViewportPolicy : IUIViewportPolicy
{
    private Vector2 _referenceResolution = new(1920f, 1080f);
    private float _userScale = 1f;

    /// <summary>Gets or sets the authored reference resolution.</summary>
    public Vector2 ReferenceResolution
    {
        get => _referenceResolution;
        set
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || value.X <= 0f || value.Y <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _referenceResolution = value;
        }
    }

    /// <summary>Gets or sets the player-selected UI scale multiplier.</summary>
    public float UserScale
    {
        get => _userScale;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _userScale = value;
        }
    }

    /// <summary>Gets or sets whether upscaling snaps to a whole framebuffer-pixel multiple.</summary>
    public bool PixelPerfect { get; set; }

    /// <summary>Gets or sets safe-area insets expressed in logical client units.</summary>
    public Thickness SafeAreaInsets { get; set; }

    /// <inheritdoc/>
    public UIViewportLayout Resolve(Vector2 clientSize, float rasterScale = 1f)
    {
        var physicalWidth = MathF.Max(1f, clientSize.X);
        var physicalHeight = MathF.Max(1f, clientSize.Y);
        var scale = MathF.Min(
            physicalWidth / ReferenceResolution.X,
            physicalHeight / ReferenceResolution.Y) * UserScale;
        rasterScale = float.IsFinite(rasterScale) && rasterScale > 0f ? rasterScale : 1f;
        var framebufferScale = scale * rasterScale;
        if (PixelPerfect && framebufferScale >= 1f)
            scale = MathF.Max(1f, MathF.Floor(framebufferScale)) / rasterScale;
        scale = MathF.Max(float.Epsilon, scale);
        var logicalSize = new Vector2(physicalWidth / scale, physicalHeight / scale);
        var safeLeft = Math.Clamp(SafeAreaInsets.Left / scale, 0f, logicalSize.X);
        var safeTop = Math.Clamp(SafeAreaInsets.Top / scale, 0f, logicalSize.Y);
        var safeRight = Math.Clamp(SafeAreaInsets.Right / scale, 0f, logicalSize.X - safeLeft);
        var safeBottom = Math.Clamp(SafeAreaInsets.Bottom / scale, 0f, logicalSize.Y - safeTop);
        return new UIViewportLayout(
            logicalSize,
            new UIClipRect(
                safeLeft,
                safeTop,
                logicalSize.X - safeRight,
                logicalSize.Y - safeBottom),
            scale);
    }
}
