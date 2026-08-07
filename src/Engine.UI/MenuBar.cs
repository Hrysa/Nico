using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Hosts horizontal menu headers and their owned context-menu popups.</summary>
public sealed class MenuBar : Panel
{
    private const float DefaultHeaderWidth = 72f;
    private readonly List<Button> _headers = [];
    private readonly List<ContextMenu> _menus = [];
    private readonly UITheme _theme;

    /// <summary>Creates an empty menu bar.</summary>
    /// <param name="width">Bar width.</param>
    /// <param name="height">Bar height.</param>
    /// <param name="theme">Theme supplying header and surface colors.</param>
    public MenuBar(float width, float height, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).SurfaceRaised, width, height)
    {
        _theme = theme ?? UITheme.Dark;
    }

    /// <summary>Adds a menu header and takes visual ownership of its popup.</summary>
    /// <param name="header">Header text.</param>
    /// <param name="menu">Context menu opened by the header.</param>
    public void AddMenu(string header, ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(menu);
        var button = new Button(DefaultHeaderWidth, Height, header, _theme);
        button.Click += () => ToggleMenu(menu);
        var menuIndex = _headers.Count;
        button.Key += (_, keyEvent) => OnHeaderKey(menuIndex, keyEvent);
        menu.Owner = button;
        menu.Close();
        _headers.Add(button);
        _menus.Add(menu);
        AddChild(button);
        AddChild(menu);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        for (var index = 0; index < _headers.Count; index++)
        {
            _headers[index].Measure(new Vector2(DefaultHeaderWidth, Height));
            _menus[index].Measure(new Vector2(_menus[index].Width, _menus[index].Height));
        }
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        for (var index = 0; index < _headers.Count; index++)
        {
            var x = index * DefaultHeaderWidth;
            _headers[index].Arrange(new Vector2(x, 0f),
                new Vector2(DefaultHeaderWidth, contentSize.Y));
            _menus[index].Arrange(new Vector2(x, contentSize.Y),
                new Vector2(_menus[index].Width, _menus[index].Height));
        }
    }

    /// <summary>Closes sibling menus and toggles the requested menu.</summary>
    /// <param name="menu">Menu associated with the clicked header.</param>
    private void ToggleMenu(ContextMenu menu)
    {
        var wasOpen = menu.IsOpen;
        for (var index = 0; index < _menus.Count; index++)
            _menus[index].Close();
        if (!wasOpen)
            menu.Open();
        InvalidateMeasure();
    }

    /// <summary>Handles menu-bar traversal and opening from a focused header.</summary>
    /// <param name="index">Header index.</param>
    /// <param name="keyEvent">Routed key input.</param>
    private void OnHeaderKey(int index, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.Kind != UIKeyEventKind.KeyDown || keyEvent.RoutePhase != UIRoutePhase.Target)
            return;
        if (keyEvent.Key is InputKey.Down or InputKey.Enter or InputKey.Space)
        {
            var menu = _menus[index];
            for (var menuIndex = 0; menuIndex < _menus.Count; menuIndex++)
                _menus[menuIndex].Close();
            menu.Open();
            menu.FocusFirst(keyEvent);
        }
        else if (keyEvent.Key is InputKey.Left or InputKey.Right)
        {
            var delta = keyEvent.Key == InputKey.Right ? 1 : -1;
            var next = (index + delta + _headers.Count) % _headers.Count;
            var keepOpen = false;
            for (var menuIndex = 0; menuIndex < _menus.Count; menuIndex++)
            {
                keepOpen |= _menus[menuIndex].IsOpen;
                _menus[menuIndex].Close();
            }
            keyEvent.Focus(_headers[next]);
            if (keepOpen)
                _menus[next].Open();
        }
        else
            return;
        keyEvent.Handled = true;
        InvalidateMeasure();
    }
}
