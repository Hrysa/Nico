using Engine.Core;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Owns the retained screen-space UI tree associated with one game scene.</summary>
public sealed class HudRoot : Node, ICustomSceneNode
{
    /// <summary>Stable scene-file type identifier for HUD roots.</summary>
    public const string SceneType = "nico/hud-root";

    private UIElement _content = CreateDefaultContent();

    /// <summary>Gets the stable scene-file type identifier.</summary>
    public string SceneTypeId => SceneType;

    /// <summary>Gets or replaces the retained tree rendered over the game viewport.</summary>
    public UIElement Content
    {
        get => _content;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_content, value))
                return;
            if (value.Parent is not null)
                throw new InvalidOperationException("HUD content must be a detached UI root.");
            var previous = _content;
            _content = PrepareContent(value);
            ContentChanged?.Invoke(previous, _content);
        }
    }

    /// <summary>Occurs after the retained HUD tree is replaced.</summary>
    public event Action<UIElement, UIElement>? ContentChanged;

    /// <summary>Creates an empty authored HUD root.</summary>
    public HudRoot()
    {
        Name = "HUD";
    }

    /// <summary>Creates the transparent default HUD surface.</summary>
    /// <returns>A viewport-filling overlay canvas.</returns>
    private static UIElement CreateDefaultContent() => PrepareContent(new Canvas());

    /// <summary>Applies screen-overlay behavior to one detached HUD tree.</summary>
    /// <param name="content">Detached retained tree.</param>
    /// <returns>The configured tree.</returns>
    private static UIElement PrepareContent(UIElement content)
    {
        content.IsOverlay = true;
        content.ClipToBounds = true;
        return content;
    }
}

/// <summary>Creates UI-owned scene node types during scene loading.</summary>
public sealed class HudSceneNodeFactory : ISceneNodeFactory
{
    /// <summary>Gets the shared stateless factory.</summary>
    public static HudSceneNodeFactory Instance { get; } = new();

    /// <summary>Prevents external construction of the stateless factory.</summary>
    private HudSceneNodeFactory()
    {
    }

    /// <inheritdoc/>
    public bool TryCreate(string sceneTypeId, out Engine.Core.Node? node)
    {
        if (string.Equals(sceneTypeId, HudRoot.SceneType, StringComparison.Ordinal))
        {
            node = new HudRoot();
            return true;
        }
        node = null;
        return false;
    }
}
