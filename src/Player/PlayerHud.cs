using System.Numerics;
using Engine.UI;

namespace PlayerApp;

/// <summary>Builds the retained screen-space HUD shared by 2D and 3D Player scenes.</summary>
public static class PlayerHud
{
    /// <summary>Creates the default lightweight runtime HUD.</summary>
    /// <returns>Viewport-sized retained HUD root.</returns>
    public static UIElement Create()
    {
        return Create(out _);
    }

    /// <summary>Creates the default HUD together with its reusable pause-menu layer.</summary>
    /// <param name="pauseMenu">Created pause-menu layer.</param>
    /// <returns>Viewport-sized retained HUD root.</returns>
    public static UIElement Create(out RuntimePauseMenu pauseMenu)
    {
        return Create(out pauseMenu, out _);
    }

    /// <summary>Creates the default HUD with pause and camera-projected world layers.</summary>
    /// <param name="pauseMenu">Created pause-menu layer.</param>
    /// <param name="worldSpaceUI">Created world-space anchor layer.</param>
    /// <returns>Viewport-sized retained HUD root.</returns>
    public static UIElement Create(
        out RuntimePauseMenu pauseMenu,
        out WorldSpaceUIHost worldSpaceUI)
    {
        var root = new Canvas { Name = "PlayerHud" };
        worldSpaceUI = new WorldSpaceUIHost();
        var worldLabel = new Label("World origin", 128f, 26f)
        {
            Name = "WorldOriginLabel",
            ForegroundColor = Engine.Graphics.Color.White,
            BackgroundColor = Engine.Graphics.Color.FromSrgb(0x18, 0x1E, 0x2A),
            FontSize = UITheme.Dark.CaptionFontSize,
            PaddingLeft = 8f,
            IsHitTestVisible = false
        };
        worldSpaceUI.Add(worldLabel, Vector3.Zero, new Vector2(0f, -12f));
        root.Add(worldSpaceUI, Vector2.Zero);
        var status = new Label("Running", 112f, 28f)
        {
            Name = "RuntimeStatus",
            ForegroundColor = Engine.Graphics.Color.White,
            FontSize = UITheme.Dark.CaptionFontSize,
            PaddingLeft = 8f,
            IsHitTestVisible = false
        };
        root.Add(status, new Vector2(16f, 16f));
        pauseMenu = new RuntimePauseMenu();
        root.Add(pauseMenu, Vector2.Zero);
        return root;
    }
}
