using System.Numerics;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Hosts one independent UI tree, renderer, input router, and native-window lifecycle.</summary>
public sealed class UIHost : IDisposable
{
    private readonly IWindow _window;
    private readonly IInputSource _input;
    private readonly IRenderer _renderer;
    private bool _disposed;

    /// <summary>Gets the root element hosted by this window.</summary>
    public UIElement Root { get; }

    /// <summary>Gets the independent input router for this UI tree.</summary>
    public UIEventRouter InputRouter { get; }

    /// <summary>Gets the latest logical pointer position reported by this window.</summary>
    public Vector2 PointerPosition { get; private set; }

    /// <summary>Creates and connects one UI host.</summary>
    /// <param name="window">Native window lifecycle.</param>
    /// <param name="input">Window input source.</param>
    /// <param name="renderer">Renderer presenting this UI tree.</param>
    /// <param name="root">Root UI element.</param>
    /// <param name="width">Initial logical width.</param>
    /// <param name="height">Initial logical height.</param>
    public UIHost(
        IWindow window,
        IInputSource input,
        IRenderer renderer,
        UIElement root,
        float width,
        float height)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(root);
        _window = window;
        _input = input;
        _renderer = renderer;
        Root = root;
        InputRouter = new UIEventRouter(root, Refresh);
        _window.Resized += OnResized;
        _input.MouseMove += OnMouseMove;
        _input.MouseDown += OnMouseDown;
        _input.MouseUp += OnMouseUp;
        _input.MouseDoubleClick += OnMouseDoubleClick;
        _input.MouseScroll += OnMouseScroll;
        _input.KeyDown += OnKeyDown;
        _input.KeyUp += OnKeyUp;
        _input.TextInput += OnTextInput;
        Resize(width, height);
    }

    /// <summary>Rebuilds the retained UI snapshot after a visual or structural change.</summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _renderer.SubmitUI(Root.BuildDrawList());
        _window.RequestFrame();
    }

    /// <summary>Measures, arranges, and submits the root at a new logical size.</summary>
    /// <param name="width">Logical client width.</param>
    /// <param name="height">Logical client height.</param>
    public void Resize(float width, float height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        width = MathF.Max(1f, width);
        height = MathF.Max(1f, height);
        Root.Width = width;
        Root.Height = height;
        Root.Measure(new Vector2(width, height));
        Root.Arrange(Vector2.Zero, new Vector2(width, height));
        _renderer.SetPushConstants(EditorUI.CreatePushConstants(width, height));
        Refresh();
    }

    /// <summary>Disconnects window and input events without owning the supplied services.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _window.Resized -= OnResized;
        _input.MouseMove -= OnMouseMove;
        _input.MouseDown -= OnMouseDown;
        _input.MouseUp -= OnMouseUp;
        _input.MouseDoubleClick -= OnMouseDoubleClick;
        _input.MouseScroll -= OnMouseScroll;
        _input.KeyDown -= OnKeyDown;
        _input.KeyUp -= OnKeyUp;
        _input.TextInput -= OnTextInput;
        GC.SuppressFinalize(this);
    }

    /// <summary>Relays native resize events into UI layout.</summary>
    /// <param name="width">Logical width.</param>
    /// <param name="height">Logical height.</param>
    private void OnResized(int width, int height) => Resize(width, height);

    /// <summary>Relays pointer movement.</summary>
    /// <param name="position">Logical pointer position.</param>
    private void OnMouseMove(Vector2 position)
    {
        PointerPosition = position;
        InputRouter.MovePointer(position);
    }

    /// <summary>Relays pointer press.</summary>
    /// <param name="button">Native button identifier.</param>
    private void OnMouseDown(int button) => InputRouter.Press();

    /// <summary>Relays pointer release.</summary>
    /// <param name="button">Native button identifier.</param>
    private void OnMouseUp(int button) => InputRouter.Release(invokeClick: true);

    /// <summary>Relays pointer double-click.</summary>
    /// <param name="button">Native button identifier.</param>
    private void OnMouseDoubleClick(int button) => InputRouter.DoubleClick();

    /// <summary>Relays pointer scrolling.</summary>
    /// <param name="offset">Wheel offset.</param>
    private void OnMouseScroll(float offset) => InputRouter.Scroll(offset);

    /// <summary>Relays keyboard press.</summary>
    /// <param name="key">Engine key.</param>
    private void OnKeyDown(InputKey key) => InputRouter.KeyDown((int)key);

    /// <summary>Relays keyboard release.</summary>
    /// <param name="key">Engine key.</param>
    private void OnKeyUp(InputKey key) => InputRouter.KeyUp((int)key);

    /// <summary>Relays text input.</summary>
    /// <param name="character">Produced character.</param>
    private void OnTextInput(char character) => InputRouter.TextInput(character);
}
