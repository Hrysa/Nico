using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Projects retained UI children from world positions into a screen-space overlay.
/// </summary>
public sealed class WorldSpaceUIHost : UIElement
{
    private readonly List<Anchor> _anchors = [];

    /// <summary>Creates a transparent, viewport-sized world anchor layer.</summary>
    public WorldSpaceUIHost()
    {
        Name = "WorldSpaceUI";
        IsHitTestVisible = false;
        IsOverlay = true;
        ClipToBounds = true;
    }

    /// <summary>Adds content anchored above a 3D world position.</summary>
    /// <param name="content">Retained UI content to project.</param>
    /// <param name="worldPosition">Position expressed in camera world space.</param>
    /// <param name="screenOffset">Additional logical screen-space offset.</param>
    public void Add(UIElement content, Vector3 worldPosition, Vector2 screenOffset = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Parent is not null)
            throw new InvalidOperationException("World-space UI content already has a parent.");
        _anchors.Add(new Anchor(content, worldPosition, screenOffset));
        AddChild(content);
    }

    /// <summary>Adds content anchored above a 2D world position at Z zero.</summary>
    /// <param name="content">Retained UI content to project.</param>
    /// <param name="worldPosition">Position expressed in 2D camera world space.</param>
    /// <param name="screenOffset">Additional logical screen-space offset.</param>
    public void Add(UIElement content, Vector2 worldPosition, Vector2 screenOffset = default) =>
        Add(content, new Vector3(worldPosition, 0f), screenOffset);

    /// <summary>Changes the world position of hosted content.</summary>
    /// <param name="content">Previously added retained content.</param>
    /// <param name="worldPosition">New position expressed in camera world space.</param>
    public void SetWorldPosition(UIElement content, Vector3 worldPosition)
    {
        var anchor = FindAnchor(content);
        if (anchor.WorldPosition == worldPosition)
            return;
        anchor.WorldPosition = worldPosition;
        anchor.ProjectionDirty = true;
    }

    /// <summary>Changes the logical screen offset applied after projection.</summary>
    /// <param name="content">Previously added retained content.</param>
    /// <param name="screenOffset">New screen-space offset.</param>
    public void SetScreenOffset(UIElement content, Vector2 screenOffset)
    {
        var anchor = FindAnchor(content);
        if (anchor.ScreenOffset == screenOffset)
            return;
        anchor.ScreenOffset = screenOffset;
        anchor.ProjectionDirty = true;
    }

    /// <summary>Removes hosted content.</summary>
    /// <param name="content">Previously added retained content.</param>
    /// <returns>True when the content was owned by this host.</returns>
    public bool Remove(UIElement content)
    {
        for (var index = 0; index < _anchors.Count; index++)
        {
            if (!ReferenceEquals(_anchors[index].Content, content))
                continue;
            _anchors.RemoveAt(index);
            RemoveChild(content);
            return true;
        }
        return false;
    }

    /// <summary>Projects every anchor through the active camera.</summary>
    /// <param name="camera">Camera used to render the associated world.</param>
    /// <param name="viewportSize">Logical UI viewport size.</param>
    /// <returns>True when projected layout or visibility changed.</returns>
    public bool UpdateProjection(ICamera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var view = camera.GetViewMatrix();
        var projection = camera.GetProjectionMatrix();
        var changed = false;
        for (var index = 0; index < _anchors.Count; index++)
        {
            var anchor = _anchors[index];
            var visible = TryProject(view, projection, anchor.WorldPosition, viewportSize,
                out var screenPosition);
            screenPosition += anchor.ScreenOffset;
            if (anchor.IsProjectedVisible == visible
                && (!visible || (!anchor.ProjectionDirty && anchor.ScreenPosition == screenPosition)))
                continue;
            anchor.IsProjectedVisible = visible;
            anchor.ScreenPosition = screenPosition;
            anchor.ProjectionDirty = false;
            anchor.Content.IsVisible = visible;
            changed = true;
        }
        if (changed)
            InvalidateArrange();
        return changed;
    }

    /// <summary>Projects one world point into logical screen coordinates.</summary>
    /// <param name="camera">Camera supplying view and projection matrices.</param>
    /// <param name="worldPosition">World position to project.</param>
    /// <param name="viewportSize">Logical UI viewport size.</param>
    /// <param name="screenPosition">Projected top-left-origin screen point.</param>
    /// <returns>True when the point lies inside the camera clip volume.</returns>
    public static bool TryProject(
        ICamera camera,
        Vector3 worldPosition,
        Vector2 viewportSize,
        out Vector2 screenPosition)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return TryProject(camera.GetViewMatrix(), camera.GetProjectionMatrix(), worldPosition,
            viewportSize, out screenPosition);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        for (var index = 0; index < _anchors.Count; index++)
            _anchors[index].Content.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        for (var index = 0; index < _anchors.Count; index++)
        {
            var anchor = _anchors[index];
            if (!anchor.IsProjectedVisible)
                continue;
            var child = anchor.Content;
            child.Measure(contentSize);
            var size = child.DesiredSize;
            var position = anchor.ScreenPosition - new Vector2(size.X * 0.5f, size.Y);
            child.Arrange(position, size);
        }
    }

    /// <summary>Finds the mutable anchor owned for content.</summary>
    /// <param name="content">Content whose anchor is required.</param>
    /// <returns>Matching anchor.</returns>
    private Anchor FindAnchor(UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        for (var index = 0; index < _anchors.Count; index++)
        {
            if (ReferenceEquals(_anchors[index].Content, content))
                return _anchors[index];
        }
        throw new InvalidOperationException("The element is not hosted by this world-space UI layer.");
    }

    /// <summary>Projects a point using already resolved camera matrices.</summary>
    /// <param name="view">World-to-view transform.</param>
    /// <param name="projection">View-to-clip transform.</param>
    /// <param name="worldPosition">World position to project.</param>
    /// <param name="viewportSize">Logical UI viewport size.</param>
    /// <param name="screenPosition">Projected top-left-origin screen point.</param>
    /// <returns>True when the point lies inside the camera clip volume.</returns>
    private static bool TryProject(
        Matrix4x4 view,
        Matrix4x4 projection,
        Vector3 worldPosition,
        Vector2 viewportSize,
        out Vector2 screenPosition)
    {
        screenPosition = default;
        if (!IsFinite(worldPosition) || !IsFinite(viewportSize)
            || viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return false;
        var viewPosition = Vector4.Transform(new Vector4(worldPosition, 1f), view);
        var clip = Vector4.Transform(viewPosition, projection);
        if (!IsFinite(clip) || clip.W <= float.Epsilon)
            return false;
        var inverseW = 1f / clip.W;
        var ndc = new Vector3(clip.X * inverseW, clip.Y * inverseW, clip.Z * inverseW);
        if (!IsFinite(ndc) || ndc.X < -1f || ndc.X > 1f
            || ndc.Y < -1f || ndc.Y > 1f || ndc.Z < 0f || ndc.Z > 1f)
            return false;
        screenPosition = new Vector2(
            (ndc.X + 1f) * 0.5f * viewportSize.X,
            (ndc.Y + 1f) * 0.5f * viewportSize.Y);
        return true;
    }

    /// <summary>Checks whether a vector contains only finite values.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    /// <summary>Checks whether a vector contains only finite values.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Checks whether a vector contains only finite values.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);

    /// <summary>Stores mutable projection state without per-frame replacement allocations.</summary>
    private sealed class Anchor
    {
        /// <summary>Creates an anchor.</summary>
        /// <param name="content">Hosted retained content.</param>
        /// <param name="worldPosition">Initial world position.</param>
        /// <param name="screenOffset">Initial screen offset.</param>
        public Anchor(UIElement content, Vector3 worldPosition, Vector2 screenOffset)
        {
            Content = content;
            WorldPosition = worldPosition;
            ScreenOffset = screenOffset;
        }

        /// <summary>Gets the hosted retained content.</summary>
        public UIElement Content { get; }

        /// <summary>Gets or sets the source world position.</summary>
        public Vector3 WorldPosition { get; set; }

        /// <summary>Gets or sets the post-projection screen offset.</summary>
        public Vector2 ScreenOffset { get; set; }

        /// <summary>Gets or sets the most recent projected screen position.</summary>
        public Vector2 ScreenPosition { get; set; }

        /// <summary>Gets or sets whether the point passed clip-volume testing.</summary>
        public bool IsProjectedVisible { get; set; } = true;

        /// <summary>Gets or sets whether source data changed since the last projection.</summary>
        public bool ProjectionDirty { get; set; } = true;
    }
}
