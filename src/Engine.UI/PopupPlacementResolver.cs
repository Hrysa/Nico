using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Stores an edge-aware popup position and its resolved direction.</summary>
/// <param name="Position">Absolute popup top-left position.</param>
/// <param name="Placement">Direction selected after optional edge flipping.</param>
internal readonly record struct PopupPlacementResult(
    Vector2 Position,
    PopupPlacement Placement);

/// <summary>Provides one edge-aware placement algorithm for overlay and nested popups.</summary>
internal static class PopupPlacementResolver
{
    /// <summary>Positions, flips, and clamps a popup against one logical work area.</summary>
    /// <param name="anchor">Absolute owner or pointer bounds.</param>
    /// <param name="popupSize">Measured popup size.</param>
    /// <param name="requestedPlacement">Preferred placement direction.</param>
    /// <param name="offset">Offset applied after directional placement.</param>
    /// <param name="workArea">Absolute logical constraint bounds.</param>
    /// <param name="constrain">Whether to flip and clamp to the work area.</param>
    /// <param name="allowFlip">Whether an owner exists for opposite-side placement.</param>
    /// <returns>The resolved absolute position and direction.</returns>
    internal static PopupPlacementResult Resolve(
        UIClipRect anchor,
        Vector2 popupSize,
        PopupPlacement requestedPlacement,
        Vector2 offset,
        UIPopupWorkArea workArea,
        bool constrain,
        bool allowFlip)
    {
        var placement = requestedPlacement;
        var position = GetPosition(anchor, popupSize, placement) + offset;
        if (constrain && allowFlip)
        {
            if (placement == PopupPlacement.Below && position.Y + popupSize.Y > workArea.Bottom)
                placement = PopupPlacement.Above;
            else if (placement == PopupPlacement.Above && position.Y < workArea.Top)
                placement = PopupPlacement.Below;
            else if (placement == PopupPlacement.Right && position.X + popupSize.X > workArea.Right)
                placement = PopupPlacement.Left;
            else if (placement == PopupPlacement.Left && position.X < workArea.Left)
                placement = PopupPlacement.Right;
            if (placement != requestedPlacement)
                position = GetPosition(anchor, popupSize, placement) + offset;
        }
        if (constrain)
        {
            position.X = Math.Clamp(position.X, workArea.Left,
                MathF.Max(workArea.Left, workArea.Right - popupSize.X));
            position.Y = Math.Clamp(position.Y, workArea.Top,
                MathF.Max(workArea.Top, workArea.Bottom - popupSize.Y));
        }
        return new PopupPlacementResult(position, placement);
    }

    /// <summary>Calculates an unconstrained directional position.</summary>
    /// <param name="anchor">Absolute owner or pointer bounds.</param>
    /// <param name="popupSize">Measured popup size.</param>
    /// <param name="placement">Requested direction.</param>
    /// <returns>Absolute popup top-left position.</returns>
    private static Vector2 GetPosition(
        UIClipRect anchor,
        Vector2 popupSize,
        PopupPlacement placement) => placement switch
    {
        PopupPlacement.Above => new Vector2(anchor.Left, anchor.Top - popupSize.Y),
        PopupPlacement.Right => new Vector2(anchor.Right, anchor.Top),
        PopupPlacement.Left => new Vector2(anchor.Left - popupSize.X, anchor.Top),
        PopupPlacement.Pointer => new Vector2(anchor.Left, anchor.Top),
        _ => new Vector2(anchor.Left, anchor.Bottom)
    };
}
