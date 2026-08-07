using Editor;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class SceneViewportInputContextTests
{
    /// <summary>Verifies a focused editor field retains repeated editing keys.</summary>
    [Fact]
    public void RouteKey_FocusedTextField_DoesNotOfferRepeatToFlyCamera()
    {
        var root = new Panel(Color.Black, 400f, 300f);
        var viewport = new ViewportPanel(200f, 200f, Color.Black);
        var field = new TextField(120f, 24f);
        root.AddChild(viewport);
        root.AddChild(field);
        var router = new UIEventRouter(root, () => { });
        router.Focus(field);
        var flyCamera = new FlyCameraController(
            new PerspectiveCamera(), _ => { }, () => { });
        using var context = new SceneViewportInputContext(viewport, flyCamera);

        var consumed = context.RouteKey(router,
            new KeyInputEvent(InputKey.Right, true, IsRepeat: true, InputModifiers.None));

        Assert.False(consumed);
        Assert.False(flyCamera.IsActive);
    }

    /// <summary>Verifies the focused Scene viewport can enter and own fly-camera mode.</summary>
    [Fact]
    public void RouteKey_FocusedSceneViewport_ActivatesAndConsumesFlyCamera()
    {
        var viewport = new ViewportPanel(200f, 200f, Color.Black);
        var router = new UIEventRouter(viewport, () => { });
        router.Focus(viewport);
        var flyCamera = new FlyCameraController(
            new PerspectiveCamera(), _ => { }, () => { });
        using var context = new SceneViewportInputContext(viewport, flyCamera);

        var consumed = context.RouteKey(router,
            new KeyInputEvent(InputKey.F, true, IsRepeat: false, InputModifiers.None));

        Assert.True(consumed);
        Assert.True(flyCamera.IsActive);
        Assert.True(context.RoutesText(router));
    }

    /// <summary>Verifies moving focus back to Editor UI releases active viewport input.</summary>
    [Fact]
    public void FocusEditorControl_AfterSceneViewport_ReleasesFlyCamera()
    {
        var root = new Panel(Color.Black, 400f, 300f);
        var viewport = new ViewportPanel(200f, 200f, Color.Black);
        var field = new TextField(120f, 24f);
        root.AddChild(viewport);
        root.AddChild(field);
        var router = new UIEventRouter(root, () => { });
        var captured = false;
        var flyCamera = new FlyCameraController(
            new PerspectiveCamera(), value => captured = value, () => { });
        using var context = new SceneViewportInputContext(viewport, flyCamera);
        router.Focus(viewport);
        context.RouteKey(router,
            new KeyInputEvent(InputKey.F, true, IsRepeat: false, InputModifiers.None));

        router.Focus(field);

        Assert.False(flyCamera.IsActive);
        Assert.False(captured);
        Assert.False(context.RoutesText(router));
    }
}
