using System.Numerics;
using System.Globalization;
using System.ComponentModel;
using Editor;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIHostTests
{
    /// <summary>Verifies a viewport texture follows retained layout movement after host refresh.</summary>
    [Fact]
    public void LayoutUpdated_MovedViewport_UpdatesPresentationQuad()
    {
        var services = new HostServices();
        var root = new Canvas();
        var viewport = new ViewportPanel(100f, 80f, Color.Black)
        {
            RenderView = new RenderViewHandle(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        root.Add(viewport, new Vector2(10f, 20f));
        var tracker = new ViewportPresentationTracker(viewport);
        using var host = new UIHost(services, services, services, root, 400f, 300f);
        host.LayoutUpdated += () => tracker.Synchronize(services);
        host.Refresh();
        var initialUpdates = services.ViewportQuadUpdateCount;

        root.SetPosition(viewport, new Vector2(160f, 110f));
        host.Refresh();

        Assert.Equal(initialUpdates + 1, services.ViewportQuadUpdateCount);
        Assert.NotNull(services.LastViewportQuad);
        Assert.Equal(new Vector3(160f, 110f, 0f), services.LastViewportQuad![0].Position);
        Assert.Equal(new Vector3(260f, 190f, 0f), services.LastViewportQuad[2].Position);
    }

    /// <summary>Verifies unchanged layout does not upload duplicate viewport geometry.</summary>
    [Fact]
    public void LayoutUpdated_UnchangedViewport_DoesNotUpdatePresentationQuad()
    {
        var services = new HostServices();
        var viewport = new ViewportPanel(100f, 80f, Color.Black)
        {
            RenderView = new RenderViewHandle(1)
        };
        var tracker = new ViewportPresentationTracker(viewport);
        using var host = new UIHost(services, services, services, viewport, 100f, 80f);
        host.LayoutUpdated += () => tracker.Synchronize(services);

        host.Refresh();
        host.Refresh();

        Assert.Equal(1, services.ViewportQuadUpdateCount);
    }

    /// <summary>Verifies an invisible tab ancestor suppresses viewport presentation.</summary>
    [Fact]
    public void LayoutUpdated_HiddenViewportAncestor_DisablesPresentationQuad()
    {
        var services = new HostServices();
        var parent = new Panel(Color.Black, 100f, 80f);
        var viewport = new ViewportPanel(100f, 80f, Color.Black)
        {
            RenderView = new RenderViewHandle(1)
        };
        parent.AddChild(viewport);
        var tracker = new ViewportPresentationTracker(viewport);
        using var host = new UIHost(services, services, services, parent, 100f, 80f);
        host.LayoutUpdated += () => tracker.Synchronize(services);
        host.Refresh();

        parent.IsVisible = false;
        host.Refresh();

        Assert.False(viewport.IsEffectivelyVisible);
        Assert.NotNull(services.LastViewportQuad);
        Assert.All(services.LastViewportQuad!, vertex => Assert.Equal(0f, vertex.Opacity));
    }

    /// <summary>Verifies activating a hidden viewport repairs its render-target resolution.</summary>
    [Fact]
    public void LayoutUpdated_ViewportBecomesVisible_ResizesRenderTarget()
    {
        var services = new HostServices();
        var parent = new Panel(Color.Black, 240f, 160f) { IsVisible = false };
        var viewport = new ViewportPanel(240f, 160f, Color.Black)
        {
            RenderView = new RenderViewHandle(1)
        };
        parent.AddChild(viewport);
        var tracker = new ViewportPresentationTracker(viewport);
        using var host = new UIHost(services, services, services, parent, 240f, 160f);
        host.LayoutUpdated += () => tracker.Synchronize(services);
        host.Refresh();

        parent.IsVisible = true;
        host.Refresh();

        Assert.Equal(1, services.RenderViewResizeCount);
        Assert.Equal(new Vector2(240f, 160f), services.LastRenderViewSize);
    }

    /// <summary>Verifies every live layout size change immediately resizes the viewport render target.</summary>
    [Fact]
    public void LayoutUpdated_ViewportSizeChanges_ResizesRenderTargetContinuously()
    {
        var services = new HostServices();
        var root = new Canvas();
        var viewport = new ViewportPanel(100f, 80f, Color.Black)
        {
            RenderView = new RenderViewHandle(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        root.Add(viewport, Vector2.Zero);
        var tracker = new ViewportPresentationTracker(viewport);
        using var host = new UIHost(services, services, services, root, 400f, 300f);
        host.LayoutUpdated += () => tracker.Synchronize(services);
        host.Refresh();
        var initialResizeCount = services.RenderViewResizeCount;

        viewport.Width = 180f;
        viewport.Height = 120f;
        host.Refresh();

        Assert.Equal(initialResizeCount + 1, services.RenderViewResizeCount);
        Assert.Equal(new Vector2(180f, 120f), services.LastRenderViewSize);
    }

    /// <summary>Verifies native magnification is exposed in logical host coordinates.</summary>
    [Fact]
    public void PointerMagnified_NativeGesture_ReachesApplicationPreview()
    {
        var services = new HostServices();
        var root = new Panel(Color.Black, 200f, 100f);
        using var host = new UIHost(services, services, services, root, 200f, 100f);
        PointerMagnifyEvent? received = null;
        host.PreviewPointerMagnify = pointerEvent =>
        {
            received = pointerEvent;
            return true;
        };

        services.RaiseMagnification(new PointerMagnifyEvent(
            0, new Vector2(50f, 25f), 0.1f, InputModifiers.Shift));

        Assert.NotNull(received);
        Assert.Equal(new Vector2(50f, 25f), received.Value.Position);
        Assert.Equal(0.1f, received.Value.Delta);
        Assert.Equal(InputModifiers.Shift, received.Value.Modifiers);
    }

    /// <summary>Verifies host-local notifications stack and expire through overlay time.</summary>
    [Fact]
    public void OverlayManager_Toasts_StackAndExpire()
    {
        var overlay = new Canvas { Width = 320f, Height = 200f };
        overlay.BuildDrawList();
        var router = new UIEventRouter(overlay, () => { });
        using var manager = new UIOverlayManager(overlay, router);

        var first = manager.Toasts.Show("Saved", ToastSeverity.Success, 0.5d);
        manager.Toasts.Show("Imported", ToastSeverity.Information, 1d);
        overlay.BuildDrawList();

        Assert.Equal(2, manager.Toasts.Count);
        Assert.True(first.Top < manager.Toasts.Children[1].Position.Y);

        Assert.True(manager.AdvanceTime(0.6d));
        overlay.BuildDrawList();

        Assert.Equal(1, manager.Toasts.Count);
    }

    /// <summary>Verifies a toast action executes and dismisses its notification.</summary>
    [Fact]
    public void OverlayManager_ActionToast_ExecutesAndDismisses()
    {
        var overlay = new Canvas { Width = 400f, Height = 200f };
        var router = new UIEventRouter(overlay, () => { });
        using var manager = new UIOverlayManager(overlay, router);
        var invoked = false;
        var toast = manager.Toasts.Show("Import complete", ToastSeverity.Success, 10d,
            "Open", () => invoked = true);
        overlay.BuildDrawList();
        var action = Assert.IsType<Button>(toast.ActionButton);

        router.MovePointer(new Vector2(action.Left + action.Width * 0.5f,
            action.Top + action.Height * 0.5f));
        router.Press();
        router.Release(invokeClick: true);

        Assert.True(invoked);
        Assert.Equal(0, manager.Toasts.Count);
    }

    /// <summary>Verifies bounded toast stacks discard the oldest notification first.</summary>
    [Fact]
    public void ToastHost_QueueLimit_DiscardsOldest()
    {
        var host = new ToastHost { MaxVisible = 2 };
        var oldest = host.Show("One");
        host.Show("Two");
        host.Show("Three");

        Assert.Equal(2, host.Count);
        Assert.DoesNotContain(oldest, host.Children);
    }

    /// <summary>Verifies hovering a notification pauses and then resumes its expiration.</summary>
    [Fact]
    public void ToastHost_Hover_PausesExpiration()
    {
        var overlay = new Canvas { Width = 320f, Height = 200f };
        var router = new UIEventRouter(overlay, () => { });
        using var manager = new UIOverlayManager(overlay, router);
        var toast = manager.Toasts.Show("Keep open", duration: 0.5d);
        toast.IsHovered = true;

        Assert.False(manager.AdvanceTime(1d));
        Assert.Equal(1, manager.Toasts.Count);

        toast.IsHovered = false;
        Assert.True(manager.AdvanceTime(0.6d));
        Assert.Equal(0, manager.Toasts.Count);
    }

    /// <summary>Verifies keyed notifications update in place and reset their expiration.</summary>
    [Fact]
    public void ToastHost_DeduplicationKey_UpdatesExistingNotification()
    {
        var host = new ToastHost();
        var first = host.Show("Importing 10%", duration: 0.5d, key: "import");
        host.Advance(0.4d);

        var updated = host.Show("Importing 80%", ToastSeverity.Success, duration: 1d, key: "import");

        Assert.Same(first, updated);
        Assert.Equal(1, host.Count);
        Assert.Equal("Importing 80%", first.Text);
        Assert.Equal(ToastSeverity.Success, first.Severity);
        Assert.False(host.Advance(0.7d));
        Assert.Equal(1, host.Count);
    }

    /// <summary>Verifies direct notification updates retain identity and can replace lifetime.</summary>
    [Fact]
    public void ToastHost_Update_ChangesRetainedNotification()
    {
        var host = new ToastHost();
        var toast = host.Show("Queued", duration: 10d);

        Assert.True(host.Update(toast, "Complete", ToastSeverity.Success, duration: 0.2d));
        Assert.Equal("Complete", toast.Text);
        Assert.True(host.Advance(0.3d));
        Assert.Equal(0, host.Count);
    }

    /// <summary>Verifies determinate toast progress clamps and updates through a retained keyed notification.</summary>
    [Fact]
    public void ToastHost_KeyedProgress_UpdatesInPlace()
    {
        var host = new ToastHost();
        var toast = host.Show("Importing", duration: 10d, key: "import", progress: 0.2f);

        var updated = host.Show("Importing", duration: 10d, key: "import", progress: 1.5f);

        Assert.Same(toast, updated);
        Assert.Equal(1f, toast.Progress);
        Assert.False(toast.IsProgressIndeterminate);
        Assert.Equal(58f, toast.Height);
    }

    /// <summary>Verifies indeterminate toast progress participates in host-time animation.</summary>
    [Fact]
    public void ToastHost_IndeterminateProgress_RequestsVisualUpdates()
    {
        var host = new ToastHost();
        var toast = host.Show("Connecting", duration: 10d, isProgressIndeterminate: true);

        Assert.True(toast.IsProgressIndeterminate);
        Assert.True(host.Advance(0.1d));

        Assert.True(host.Update(toast, "Connected", ToastSeverity.Success,
            progress: 1f, isProgressIndeterminate: false));
        Assert.False(toast.IsProgressIndeterminate);
        Assert.Equal(1f, toast.Progress);
    }

    /// <summary>Verifies owner hover opens and closes a delayed tooltip using host time.</summary>
    [Fact]
    public void Host_Update_AdvancesDelayedToolTip()
    {
        var services = new HostServices();
        var root = new Canvas();
        var owner = new Button(100f, 30f, "Hover");
        root.Add(owner, new Vector2(10f, 10f));
        using var toolTip = new ToolTip(owner, root, "Helpful") { Delay = 0.2 };
        using var host = new UIHost(services, services, services, root, 320f, 200f);

        services.RaiseMove(new Vector2(20f, 20f));
        services.PumpFrame(0.1);
        Assert.False(toolTip.IsOpen);
        services.PumpFrame(0.15);
        Assert.True(toolTip.IsOpen);

        services.RaiseMove(new Vector2(250f, 150f));
        Assert.False(toolTip.IsOpen);
    }

    /// <summary>Verifies indeterminate progress requests retained submissions from host time.</summary>
    [Fact]
    public void Host_Update_AdvancesIndeterminateProgress()
    {
        var services = new HostServices();
        var progress = new ProgressBar(100f, 10f) { IsIndeterminate = true };
        using var host = new UIHost(services, services, services, progress, 100f, 10f);
        var initialSubmissions = services.SubmitCount;

        services.PumpFrame(0.25);

        Assert.True(services.SubmitCount > initialSubmissions);
    }

    /// <summary>Verifies retained invalidation schedules and submits a frame without another input event.</summary>
    [Fact]
    public void Host_VisualInvalidation_RequestsAndSubmitsNextFrame()
    {
        var services = new HostServices();
        var root = new Panel(Color.Black);
        using var host = new UIHost(services, services, services, root, 320f, 200f);
        services.PumpFrame(0d);
        var requests = services.RequestFrameCount;
        var submissions = services.SubmitCount;

        root.BackgroundColor = Color.Red;

        Assert.Equal(requests + 1, services.RequestFrameCount);
        services.PumpFrame(0d);
        Assert.Equal(submissions + 1, services.SubmitCount);
        services.PumpFrame(0d);
        Assert.Equal(submissions + 1, services.SubmitCount);
    }

    /// <summary>Verifies several retained mutations share one pending host wake.</summary>
    [Fact]
    public void Host_MultipleInvalidations_CoalesceFrameRequest()
    {
        var services = new HostServices();
        var root = new Panel(Color.Black);
        using var host = new UIHost(services, services, services, root, 320f, 200f);
        services.PumpFrame(0d);
        var requests = services.RequestFrameCount;

        root.BackgroundColor = Color.Red;
        root.ForegroundColor = Color.White;
        root.Padding = new Thickness(4f);

        Assert.Equal(requests + 1, services.RequestFrameCount);
    }

    /// <summary>Verifies high-frequency touchpad deltas share one retained snapshot rebuild.</summary>
    [Fact]
    public void Host_PointerWheel_CoalescesSnapshotUntilNextFrame()
    {
        var services = new VersionedHostServices();
        var viewer = new ScrollViewer(100f, 100f)
        {
            Content = new Panel(Color.Red, 100f, 300f)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            }
        };
        using var host = new UIHost(services, services, services, viewer, 100f, 100f);
        services.PumpFrame(0d);
        var submissions = services.SubmitCount;

        services.RaiseWheel(new Vector2(20f, 20f), new Vector2(0f, -0.25f));
        services.RaiseWheel(new Vector2(20f, 20f), new Vector2(0f, -0.25f));
        services.RaiseWheel(new Vector2(20f, 20f), new Vector2(0f, -0.25f));

        Assert.Equal(24f, viewer.VerticalOffset);
        Assert.Equal(submissions, services.SubmitCount);
        services.PumpFrame(0d);
        Assert.Equal(submissions + 1, services.SubmitCount);
    }

    /// <summary>Verifies rebuilding a hosted dock tree paints on the next tick without new input.</summary>
    [Fact]
    public void Host_DockRefresh_SubmitsNextFrameWithoutInput()
    {
        var services = new HostServices();
        var workspace = new DockWorkspace
        {
            Root = new DockTabGroup([new DockTab("scene", "Scene")])
        };
        var content = new Panel(Color.Black);
        var root = new DockHost(workspace, id => id == "scene" ? content : null);
        using var host = new UIHost(services, services, services, root, 320f, 200f);
        services.PumpFrame(0d);
        var submissions = services.SubmitCount;

        root.Refresh();
        services.PumpFrame(0d);

        Assert.Equal(submissions + 1, services.SubmitCount);
    }

    /// <summary>Verifies held repeat buttons advance from host update time and stop on release.</summary>
    [Fact]
    public void Host_Update_AdvancesHeldRepeatButton()
    {
        var services = new HostServices();
        var root = new Canvas();
        var button = new RepeatButton(100f, 30f, "+") { Delay = 0.2, Interval = 0.1 };
        var invocations = 0;
        button.Click += () => invocations++;
        root.Add(button, new Vector2(10f, 10f));
        using var host = new UIHost(services, services, services, root, 320f, 200f);

        services.RaisePress(new Vector2(20f, 20f));
        services.PumpFrame(0.35);
        services.RaiseRelease();
        services.PumpFrame(1d);

        Assert.Equal(3, invocations);
        Assert.Null(host.InputRouter.CapturedElement);
    }

    /// <summary>Verifies each host owns layout, submission, and input routing for its own root.</summary>
    [Fact]
    public void Host_ResizeAndInput_RoutesIndependentTree()
    {
        var services = new HostServices();
        var root = new Canvas();
        var button = new Button(100f, 30f, "Open");
        root.Add(button, new Vector2(10f, 10f));
        var clicked = false;
        button.Click += () => clicked = true;
        using var host = new UIHost(services, services, services, root, 320f, 200f);

        services.RaiseResize(640, 480);
        services.RaiseClick(new Vector2(20f, 20f));

        Assert.Equal(640f, root.Width);
        Assert.Equal(480f, root.Height);
        Assert.True(clicked);
        Assert.True(services.SubmitCount >= 2);
    }

    /// <summary>Verifies hosted text controls inherit one measurement and caret service.</summary>
    [Fact]
    public void Host_TextLayout_IsInheritedByLabelsAndEditors()
    {
        var services = new HostServices();
        var textLayout = new FixedTextLayoutService();
        var root = new Canvas();
        var label = new Label("abc", height: 20f);
        var field = new TextField(100f, 30f) { Text = "abcdefgh" };
        root.Add(label, Vector2.Zero);
        root.Add(field, new Vector2(0f, 40f));
        label.Measure(new Vector2(320f, 200f));
        var fallbackWidth = label.DesiredSize.X;
        using var host = new UIHost(
            services, services, services, root, 320f, 200f, textLayout: textLayout);

        Assert.Same(textLayout, label.TextLayout);
        Assert.Equal(33f, label.MeasureTextWidth());
        Assert.Equal(37f, label.DesiredSize.X);
        Assert.NotEqual(fallbackWidth, label.DesiredSize.X);

        services.RaisePress(new Vector2(20f, 50f));
        services.RaiseRelease();

        Assert.Equal(3, field.CaretIndex);
        Assert.True(textLayout.HitTestCount > 0);
    }

    /// <summary>Verifies worker-posted callbacks execute on the host thread during update.</summary>
    [Fact]
    public void Dispatcher_PostFromWorker_DrainsOnHostUpdate()
    {
        var services = new HostServices();
        using var host = new UIHost(services, services, services, new Canvas(), 320f, 200f);
        var callbackThread = 0;
        var ownerThread = Environment.CurrentManagedThreadId;

        var worker = new Thread(() => host.Dispatcher.Post(
            () => callbackThread = Environment.CurrentManagedThreadId));
        worker.Start();
        worker.Join();
        Assert.Equal(0, callbackThread);

        services.PumpFrame();

        Assert.Equal(ownerThread, callbackThread);
    }

    /// <summary>Verifies hosted asynchronous validation applies completion state on the UI thread.</summary>
    [Fact]
    public async Task AsyncValidation_WorkerCompletion_MarshalsThroughHostDispatcher()
    {
        var services = new HostServices();
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var field = new TextField(180f, 30f)
        {
            Text = "value",
            AsyncValidator = async (_, token) => await completion.Task.WaitAsync(token)
        };
        using var host = new UIHost(services, services, services, field, 320f, 200f);
        var ownerThread = Environment.CurrentManagedThreadId;
        var completionThread = 0;
        field.ValidationChanged += _ => completionThread = Environment.CurrentManagedThreadId;
        var validation = field.ValidateAsync().AsTask();

        var worker = new Thread(() => completion.SetResult("Unavailable"));
        worker.Start();
        worker.Join();
        Assert.True(field.IsValidationPending);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            services.PumpFrame();
            return validation.IsCompleted;
        }, TimeSpan.FromSeconds(5)));

        Assert.False(await validation);
        Assert.Equal(ownerThread, completionThread);
        Assert.Equal("Unavailable", field.ValidationMessage);
    }

    /// <summary>Verifies hosted debounce validates only the latest rapid text generation.</summary>
    [Fact]
    public void AsyncValidation_Debounce_CollapsesRapidTextChanges()
    {
        var services = new HostServices();
        var calls = 0;
        var validatedText = string.Empty;
        var field = new TextField(180f, 30f)
        {
            AsyncValidationDelay = TimeSpan.FromMilliseconds(20),
            AsyncValidator = (text, _) =>
            {
                calls++;
                validatedText = text;
                return ValueTask.FromResult<string?>(null);
            }
        };
        using var host = new UIHost(services, services, services, field, 320f, 200f);
        host.InputRouter.Focus(field);

        host.InputRouter.RouteText("a");
        host.InputRouter.RouteText("b");
        host.InputRouter.RouteText("c");

        Assert.True(SpinWait.SpinUntil(() =>
        {
            services.PumpFrame();
            return calls == 1 && !field.IsValidationPending;
        }, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
        Assert.Equal("abc", validatedText);
    }

    /// <summary>Verifies direct UI access from a worker thread is rejected.</summary>
    [Fact]
    public void Resize_FromWorkerThread_Throws()
    {
        var services = new HostServices();
        using var host = new UIHost(services, services, services, new Canvas(), 320f, 200f);

        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                host.Resize(640f, 480f);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        worker.Start();
        worker.Join();

        Assert.IsType<InvalidOperationException>(failure);
    }

    /// <summary>Verifies a host prefers versioned input and routes its logical pointer data.</summary>
    [Fact]
    public void Host_VersionedInput_RoutesPointerClick()
    {
        var services = new VersionedHostServices();
        var root = new Canvas();
        var button = new Button(100f, 30f, "Open");
        root.Add(button, new Vector2(10f, 10f));
        var clicked = false;
        button.Click += () => clicked = true;
        using var host = new UIHost(services, services, services, root, 320f, 200f);

        services.RaiseVersionedClick(new Vector2(20f, 20f));

        Assert.True(clicked);
        Assert.Equal(new Vector2(20f, 20f), host.PointerPosition);
    }

    /// <summary>Verifies reference scaling expands logical space without changing authored scale.</summary>
    [Fact]
    public void ReferenceViewportPolicy_WideClient_ExpandsLogicalWidthAndAppliesSafeArea()
    {
        var policy = new ReferenceResolutionUIViewportPolicy
        {
            ReferenceResolution = new Vector2(1920f, 1080f),
            SafeAreaInsets = new Thickness(100f, 50f, 200f, 25f)
        };

        var layout = policy.Resolve(new Vector2(2560f, 1080f));

        Assert.Equal(1f, layout.Scale);
        Assert.Equal(new Vector2(2560f, 1080f), layout.LogicalSize);
        Assert.Equal(new UIClipRect(100f, 50f, 2360f, 1055f), layout.ContentBounds);
    }

    /// <summary>Verifies pixel-perfect scaling accounts for physical framebuffer density.</summary>
    [Fact]
    public void ReferenceViewportPolicy_PixelPerfect_SnapsFramebufferScale()
    {
        var policy = new ReferenceResolutionUIViewportPolicy
        {
            ReferenceResolution = new Vector2(1920f, 1080f),
            PixelPerfect = true
        };

        var layout = policy.Resolve(new Vector2(3840f, 2160f), rasterScale: 1.25f);

        Assert.Equal(1.6f, layout.Scale, 3);
        Assert.Equal(new Vector2(2400f, 1350f), layout.LogicalSize);
    }

    /// <summary>Verifies UIHost maps client input and safe content into reference-resolution coordinates.</summary>
    [Fact]
    public void Host_ReferenceViewportPolicy_MapsLayoutAndPointerInput()
    {
        var services = new HostServices();
        var root = new Canvas();
        var button = new Button(100f, 30f, "Open");
        root.Add(button, new Vector2(10f, 10f));
        var clicked = false;
        button.Click += () => clicked = true;
        var policy = new ReferenceResolutionUIViewportPolicy
        {
            ReferenceResolution = new Vector2(320f, 200f),
            SafeAreaInsets = new Thickness(20f, 10f, 0f, 0f)
        };
        using var host = new UIHost(
            services, services, services, root, 640f, 400f, viewportPolicy: policy);

        services.RaiseClick(new Vector2(50f, 30f));

        Assert.True(clicked);
        Assert.Equal(new Vector2(25f, 15f), host.PointerPosition);
        Assert.Equal(10f, root.Left);
        Assert.Equal(5f, root.Top);
        Assert.Equal(310f, root.Width);
        Assert.Equal(195f, root.Height);
    }

    /// <summary>Verifies a runtime user-scale change performs one explicit retained resubmission.</summary>
    [Fact]
    public void Host_RefreshViewportPolicy_ReappliesMutableUserScale()
    {
        var services = new HostServices();
        var root = new Canvas();
        var policy = new ReferenceResolutionUIViewportPolicy
        {
            ReferenceResolution = new Vector2(320f, 200f)
        };
        using var host = new UIHost(
            services, services, services, root, 640f, 400f, viewportPolicy: policy);
        var submissions = services.SubmitCount;

        policy.UserScale = 2f;
        host.RefreshViewportPolicy();

        Assert.Equal(160f, root.Width);
        Assert.Equal(100f, root.Height);
        Assert.Equal(submissions + 1, services.SubmitCount);
    }

    /// <summary>Verifies controller directions choose spatial neighbors and submit activates focus.</summary>
    [Fact]
    public void Host_ControllerNavigation_MovesSpatialFocusAndSubmits()
    {
        var services = new NavigationHostServices();
        var root = new Canvas();
        var left = new Button(80f, 30f, "Left");
        var right = new Button(80f, 30f, "Right");
        var lower = new Button(80f, 30f, "Lower");
        root.Add(left, new Vector2(10f, 10f));
        root.Add(right, new Vector2(150f, 10f));
        root.Add(lower, new Vector2(150f, 100f));
        var clicked = false;
        lower.Click += () => clicked = true;
        using var host = new UIHost(
            services, services, services, root, 320f, 200f,
            inputContext: UIInputContextMode.Shared);

        services.RaiseNavigation(UINavigationAction.Right);
        Assert.Same(left, host.InputRouter.FocusedElement);
        services.RaiseNavigation(UINavigationAction.Right);
        Assert.Same(right, host.InputRouter.FocusedElement);
        services.RaiseNavigation(UINavigationAction.Down);
        Assert.Same(lower, host.InputRouter.FocusedElement);
        services.RaiseNavigation(UINavigationAction.Submit);

        Assert.True(clicked);
    }

    /// <summary>Verifies gameplay-only, shared, and exclusive UI input arbitration.</summary>
    [Fact]
    public void Host_InputContext_ArbitratesControllerAndGameplayInput()
    {
        var services = new NavigationHostServices();
        var button = new Button(80f, 30f, "Resume");
        var clicked = false;
        button.Click += () => clicked = true;
        using var host = new UIHost(
            services, services, services, button, 320f, 200f,
            inputContext: UIInputContextMode.GameplayOnly);
        var processed = 0;
        var handled = false;
        host.NavigationProcessed += (_, wasHandled) =>
        {
            processed++;
            handled = wasHandled;
        };

        services.RaiseNavigation(UINavigationAction.Submit);
        Assert.False(clicked);
        Assert.False(handled);
        Assert.True(host.AllowsGameplayInput);

        host.InputContext = UIInputContextMode.Shared;
        services.RaiseNavigation(UINavigationAction.Right);
        services.RaiseNavigation(UINavigationAction.Submit);
        Assert.True(clicked);
        Assert.True(handled);
        Assert.True(host.AllowsGameplayInput);

        host.InputContext = UIInputContextMode.UIExclusive;
        Assert.False(host.AllowsGameplayInput);
        Assert.Equal(3, processed);
    }

    /// <summary>Verifies held controller directions repeat on unscaled host update time.</summary>
    [Fact]
    public void Host_ControllerNavigation_HeldDirectionRepeatsUntilRelease()
    {
        var services = new NavigationHostServices();
        var root = new Canvas();
        var first = new Button(60f, 30f, "One");
        var second = new Button(60f, 30f, "Two");
        var third = new Button(60f, 30f, "Three");
        root.Add(first, new Vector2(10f, 10f));
        root.Add(second, new Vector2(100f, 10f));
        root.Add(third, new Vector2(190f, 10f));
        using var host = new UIHost(
            services, services, services, root, 280f, 80f,
            inputContext: UIInputContextMode.Shared)
        {
            NavigationRepeatDelay = 0.4d,
            NavigationRepeatInterval = 0.1d
        };

        services.RaiseNavigation(UINavigationAction.Right);
        Assert.Same(first, host.InputRouter.FocusedElement);
        services.PumpFrame(0.41d);
        Assert.Same(second, host.InputRouter.FocusedElement);
        services.PumpFrame(0.1d);
        Assert.Same(third, host.InputRouter.FocusedElement);

        services.ReleaseNavigation(UINavigationAction.Right);
        services.PumpFrame(1d);
        Assert.Same(third, host.InputRouter.FocusedElement);
    }

    /// <summary>Verifies scaled UI behavior pauses while default unscaled behavior continues.</summary>
    [Fact]
    public void Host_SeparateClocks_PauseOnlyScaledSubtree()
    {
        var services = new HostServices();
        var root = new Panel(Color.Black);
        var unscaled = new ClockProbe();
        var scaled = new ClockProbe { Clock = UIClockKind.Scaled };
        root.AddChild(unscaled);
        root.AddChild(scaled);
        using var host = new UIHost(services, services, services, root, 320f, 200f)
        {
            SimulationTimeScale = 0d
        };

        services.PumpFrame(0.5d);

        Assert.Equal(0.5d, unscaled.Elapsed);
        Assert.Equal(0d, scaled.Elapsed);
    }

    /// <summary>Verifies continuous and event-driven hosts explicitly own recurring updates.</summary>
    [Fact]
    public void Host_SchedulingMode_AppliesContinuousAndEventDrivenPolicies()
    {
        var continuousServices = new HostServices();
        using (var host = new UIHost(
            continuousServices, continuousServices, continuousServices,
            new Canvas(), 320f, 200f,
            schedulingMode: UIHostSchedulingMode.Continuous))
        {
            Assert.True(continuousServices.ContinuousRendering);
        }
        Assert.False(continuousServices.ContinuousRendering);

        var eventServices = new HostServices();
        using var eventHost = new UIHost(
            eventServices, eventServices, eventServices,
            new Canvas(), 320f, 200f,
            schedulingMode: UIHostSchedulingMode.EventDriven);
        Assert.False(eventServices.ContinuousRendering);
    }

    /// <summary>Verifies captured splitter-style input pumps continuously until release.</summary>
    [Fact]
    public void Host_EventDrivenPointerCapture_UsesContinuousPumpingUntilRelease()
    {
        var services = new VersionedHostServices();
        var thumb = new Thumb();
        using var host = new UIHost(
            services, services, services, thumb, 20f, 100f,
            schedulingMode: UIHostSchedulingMode.EventDriven);

        services.RaiseVersionedPress(new Vector2(10f, 50f));

        Assert.Same(thumb, host.InputRouter.CapturedElement);
        Assert.True(host.RequiresContinuousUpdates);
        Assert.True(services.ContinuousRendering);

        services.RaiseVersionedMove(new Vector2(14f, 50f));
        Assert.True(services.ContinuousRendering);
        Assert.Equal(1, services.InteractiveFrameCount);

        services.RaiseVersionedRelease(new Vector2(14f, 50f));

        Assert.Null(host.InputRouter.CapturedElement);
        Assert.False(host.RequiresContinuousUpdates);
        Assert.False(services.ContinuousRendering);
    }

    /// <summary>Verifies a dragging thumb retains its resize cursor outside its hover bounds.</summary>
    [Fact]
    public void Host_CapturedThumb_RetainsCursorUntilRelease()
    {
        var services = new VersionedHostServices();
        var root = new Canvas();
        var thumb = new Thumb { Width = 20f, Height = 100f, CursorKind = PointerCursorKind.HorizontalResize };
        root.Add(thumb, Vector2.Zero);
        using var host = new UIHost(services, services, services, root, 200f, 100f);

        services.RaiseVersionedMove(new Vector2(10f, 50f));
        Assert.Equal(PointerCursorKind.HorizontalResize, services.PointerCursor);

        services.RaiseVersionedPress(new Vector2(10f, 50f));
        services.RaiseVersionedMove(new Vector2(150f, 50f));
        Assert.Equal(PointerCursorKind.HorizontalResize, services.PointerCursor);

        services.RaiseVersionedRelease(new Vector2(150f, 50f));
        Assert.Equal(PointerCursorKind.Default, services.PointerCursor);
    }

    /// <summary>Verifies consumed movement clears a stale retained-UI cursor hint.</summary>
    [Fact]
    public void Host_ConsumedPointerMove_ResetsCursor()
    {
        var services = new VersionedHostServices();
        var thumb = new Thumb { CursorKind = PointerCursorKind.VerticalResize };
        using var host = new UIHost(services, services, services, thumb, 20f, 100f);

        services.RaiseVersionedMove(new Vector2(10f, 50f));
        Assert.Equal(PointerCursorKind.VerticalResize, services.PointerCursor);

        host.PreviewPointerMove = _ => true;
        services.RaiseVersionedMove(new Vector2(10f, 50f));

        Assert.Equal(PointerCursorKind.Default, services.PointerCursor);
    }

    /// <summary>Verifies host disposal restores a cursor it changed.</summary>
    [Fact]
    public void Host_Dispose_RestoresDefaultCursor()
    {
        var services = new HostServices();
        var thumb = new Thumb { CursorKind = PointerCursorKind.HorizontalResize };
        var host = new UIHost(services, services, services, thumb, 20f, 100f);
        services.RaiseMove(new Vector2(10f, 50f));

        host.Dispose();

        Assert.Equal(PointerCursorKind.Default, services.PointerCursor);
    }

    /// <summary>Verifies UI snapshot submission does not replace the application's clear color.</summary>
    [Fact]
    public void Host_Refresh_DoesNotOverrideRendererClearColor()
    {
        var services = new HostServices();
        services.SetUiClearColor(0.1f, 0.2f, 0.3f);
        using var host = new UIHost(
            services, services, services, new Panel(Color.Red), 100f, 100f);

        host.Refresh();

        Assert.Equal(1, services.UiClearColorSetCount);
    }

    /// <summary>Verifies externally managed hosts expose pointer-capture scheduling demand.</summary>
    [Fact]
    public void Host_ExternalPointerCapture_ReportsContinuousUpdateDemand()
    {
        var services = new HostServices();
        var thumb = new Thumb();
        using var host = new UIHost(services, services, services, thumb, 20f, 100f);

        services.RaisePress(new Vector2(10f, 50f));
        Assert.True(host.RequiresContinuousUpdates);

        services.RaiseRelease();
        Assert.False(host.RequiresContinuousUpdates);
    }

    /// <summary>Verifies held arrow keys synthesize repeat presses and stop immediately on release.</summary>
    [Fact]
    public void Host_EventDrivenKeyboardRepeat_MovesTextCaretContinuously()
    {
        var services = new VersionedHostServices();
        var field = new TextField(180f, 30f) { Text = "abcdef" };
        using var host = new UIHost(
            services, services, services, field, 180f, 30f,
            schedulingMode: UIHostSchedulingMode.EventDriven)
        {
            KeyRepeatDelay = 0.1d,
            KeyRepeatInterval = 0.05d
        };
        host.InputRouter.Focus(field);
        host.InputRouter.RouteKey(new KeyInputEvent(
            InputKey.Home, true, false, InputModifiers.None));
        var repeats = 0;
        host.KeyProcessed += keyEvent => repeats += keyEvent.IsRepeat ? 1 : 0;

        services.RaiseKey(new KeyInputEvent(
            InputKey.Right, true, false, InputModifiers.None));
        Assert.Equal(1, field.CaretIndex);
        Assert.True(services.ContinuousRendering);

        services.PumpFrame(0.1d);
        services.PumpFrame(0.05d);
        Assert.Equal(3, field.CaretIndex);
        Assert.Equal(2, repeats);

        services.RaiseKey(new KeyInputEvent(
            InputKey.Right, false, false, InputModifiers.None));
        Assert.False(services.ContinuousRendering);
        services.PumpFrame(1d);
        Assert.Equal(3, field.CaretIndex);
    }

    /// <summary>Verifies an idle wake starts rather than consumes the initial repeat delay.</summary>
    [Fact]
    public void Host_EventDrivenKeyboardRepeat_IdleWakePreservesInitialDelay()
    {
        var services = new VersionedHostServices();
        var field = new TextField(180f, 30f) { Text = "abcdef" };
        using var host = new UIHost(
            services, services, services, field, 180f, 30f,
            schedulingMode: UIHostSchedulingMode.EventDriven)
        {
            KeyRepeatDelay = 0.1d,
            KeyRepeatInterval = 0.05d
        };
        host.InputRouter.Focus(field);
        host.InputRouter.RouteKey(new KeyInputEvent(
            InputKey.Home, true, false, InputModifiers.None));

        services.RaiseKey(new KeyInputEvent(
            InputKey.Right, true, false, InputModifiers.None));
        services.PumpFrame(0d);
        services.PumpFrame(0.099d);

        Assert.Equal(1, field.CaretIndex);
        services.PumpFrame(0.001d);
        Assert.Equal(2, field.CaretIndex);
    }

    /// <summary>Verifies repeat-enabled editing commands delete continuously while held.</summary>
    [Fact]
    public void Host_KeyboardRepeat_RepeatsBackspaceEditingCommand()
    {
        var services = new VersionedHostServices();
        var field = new TextField(180f, 30f) { Text = "abcdef" };
        using var host = new UIHost(
            services, services, services, field, 180f, 30f,
            schedulingMode: UIHostSchedulingMode.Continuous)
        {
            KeyRepeatDelay = 0.1d,
            KeyRepeatInterval = 0.05d
        };
        host.InputRouter.Focus(field);
        host.InputRouter.RouteKey(new KeyInputEvent(
            InputKey.End, true, false, InputModifiers.None));

        services.RaiseKey(new KeyInputEvent(
            InputKey.Backspace, true, false, InputModifiers.None));
        services.PumpFrame(0.2d);
        services.RaiseKey(new KeyInputEvent(
            InputKey.Backspace, false, false, InputModifiers.None));

        Assert.Equal("abcd", field.Text);
    }

    /// <summary>Verifies native repeat disables synthesis for that hold to prevent duplicate events.</summary>
    [Fact]
    public void Host_NativeKeyboardRepeat_DisablesSyntheticRepeatForCurrentHold()
    {
        var services = new VersionedHostServices();
        var target = new Control(100f, 30f) { IsTabStop = true };
        using var host = new UIHost(
            services, services, services, target, 100f, 30f,
            schedulingMode: UIHostSchedulingMode.EventDriven)
        {
            KeyRepeatDelay = 0.1d,
            KeyRepeatInterval = 0.05d
        };
        host.InputRouter.Focus(target);
        var repeats = 0;
        target.Key += (_, keyEvent) => repeats += keyEvent.IsRepeat ? 1 : 0;
        services.RaiseKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.None));
        Assert.True(services.ContinuousRendering);

        services.RaiseKey(new KeyInputEvent(InputKey.A, true, true, InputModifiers.None));
        Assert.False(services.ContinuousRendering);
        services.PumpFrame(1d);

        Assert.Equal(1, repeats);
        services.RaiseKey(new KeyInputEvent(InputKey.A, false, false, InputModifiers.None));
    }

    /// <summary>Verifies the reusable repeat policy emits after its delay and stops on release.</summary>
    [Fact]
    public void KeyboardRepeatController_HeldKey_EmitsUntilReleased()
    {
        var controller = new UIKeyRepeatController
        {
            Delay = 0.1d,
            Interval = 0.05d
        };
        var repeats = new List<KeyInputEvent>();
        controller.Observe(new KeyInputEvent(
            InputKey.Right, true, false, InputModifiers.Shift));

        controller.Advance(0.1d, repeats.Add);
        controller.Advance(0.05d, repeats.Add);

        Assert.Equal(2, repeats.Count);
        Assert.All(repeats, repeat =>
        {
            Assert.Equal(InputKey.Right, repeat.Key);
            Assert.True(repeat.IsRepeat);
            Assert.Equal(InputModifiers.Shift, repeat.Modifiers);
        });
        controller.Observe(new KeyInputEvent(
            InputKey.Right, false, false, InputModifiers.Shift));
        Assert.False(controller.IsRepeatPending);
        controller.Advance(1d, repeats.Add);
        Assert.Equal(2, repeats.Count);
    }

    /// <summary>Verifies an idle update cannot replay accumulated repeats in one frame.</summary>
    [Fact]
    public void KeyboardRepeatController_LongIdle_EmitsOnlyOneRepeat()
    {
        var controller = new UIKeyRepeatController
        {
            Delay = 0.1d,
            Interval = 0.05d
        };
        var repeats = new List<KeyInputEvent>();
        controller.Observe(new KeyInputEvent(
            InputKey.Right, true, false, InputModifiers.None));

        controller.Advance(5d, repeats.Add);

        Assert.Single(repeats);
    }

    /// <summary>Verifies application previews can intercept versioned input around standard routing.</summary>
    [Fact]
    public void Host_InputPreview_ControlsPointerAndKeyboardRouting()
    {
        var services = new VersionedHostServices();
        var button = new Button(100f, 30f, "Action") { IsTabStop = true };
        using var host = new UIHost(services, services, services, button, 100f, 30f);
        host.InputRouter.Focus(button);
        var clicks = 0;
        var keyDown = 0;
        var processedMoves = 0;
        button.Click += () => clicks++;
        button.KeyDown += _ => keyDown++;
        host.PreviewPointerButton = pointerEvent => pointerEvent.IsPressed
            ? UIHostPointerRouting.Route
            : UIHostPointerRouting.RouteWithoutClick;
        host.PointerMoveProcessed = (_, routed) => processedMoves += routed ? 1 : 0;
        host.PreviewKey = _ => true;

        services.RaiseVersionedClick(new Vector2(10f, 10f));
        services.RaiseKey(new KeyInputEvent(InputKey.A, true, false, InputModifiers.Control));

        Assert.Equal(0, clicks);
        Assert.Equal(0, keyDown);
        Assert.Equal(1, processedMoves);
    }

    /// <summary>Verifies a host routes committed Unicode text and IME composition to its focused editor.</summary>
    [Fact]
    public void Host_VersionedTextAndComposition_RouteToFocusedTextField()
    {
        var services = new VersionedHostServices();
        var field = new TextField(180f, 30f);
        using var host = new UIHost(services, services, services, field, 180f, 30f);
        host.InputRouter.Focus(field);

        services.RaiseText("中文");
        services.RaiseComposition(new TextCompositionEvent(
            TextCompositionKind.Started, "拼", 1, 0, 1));

        Assert.Equal("中文", field.Text);
        Assert.True(field.IsComposing);
        Assert.Equal("拼", field.CompositionText);

        services.RaiseComposition(new TextCompositionEvent(
            TextCompositionKind.Completed, "拼", 1));

        Assert.Equal("中文拼", field.Text);
        Assert.False(field.IsComposing);
    }

    /// <summary>Verifies hybrid hosts run only while retained animation requires host time.</summary>
    [Fact]
    public void Host_HybridScheduling_FollowsActiveRetainedAnimation()
    {
        var services = new HostServices();
        var progress = new ProgressBar(100f, 10f) { IsIndeterminate = true };
        using var host = new UIHost(
            services, services, services, progress, 100f, 10f,
            schedulingMode: UIHostSchedulingMode.Hybrid);

        Assert.True(services.ContinuousRendering);

        host.SetMotionPreference(UIMotionPreference.Reduced);
        services.PumpFrame(0.1d);

        Assert.False(services.ContinuousRendering);
    }

    /// <summary>Verifies newly started keyed animations activate hybrid recurring updates.</summary>
    [Fact]
    public void Host_HybridScheduling_ActivatesForOwnedAnimation()
    {
        var services = new HostServices();
        var root = new Canvas();
        using var host = new UIHost(
            services, services, services, root, 100f, 10f,
            schedulingMode: UIHostSchedulingMode.Hybrid);
        Assert.False(services.ContinuousRendering);

        root.StartAnimation("fade", new UIFloatAnimation(0f, 1f, 2d, _ => { }));
        services.PumpFrame(0.1d);

        Assert.True(services.ContinuousRendering);
    }

    /// <summary>Verifies a direct retained timer-state change invalidates the hybrid activity cache.</summary>
    [Fact]
    public void Host_HybridScheduling_ActivatesAfterProgressStateChange()
    {
        var services = new HostServices();
        var progress = new ProgressBar(100f, 10f);
        using var host = new UIHost(
            services, services, services, progress, 100f, 10f,
            schedulingMode: UIHostSchedulingMode.Hybrid);
        Assert.False(services.ContinuousRendering);

        progress.IsIndeterminate = true;
        services.PumpFrame(0.1d);

        Assert.True(services.ContinuousRendering);
    }

    /// <summary>Verifies host shutdown cancels animations throughout its retained ownership tree.</summary>
    [Fact]
    public void Host_Dispose_CancelsOwnedAnimations()
    {
        var services = new HostServices();
        var root = new Canvas();
        var child = new UIElement();
        root.AddChild(child);
        var animation = new UIFloatAnimation(0f, 1f, 2d, _ => { });
        child.StartAnimation("fade", animation);
        var host = new UIHost(services, services, services, root, 100f, 10f);

        host.Dispose();

        Assert.True(animation.IsCancelled);
        Assert.False(animation.IsRunning);
        Assert.Equal(0, child.ActiveAnimationCount);
    }

    /// <summary>Verifies routed drag completion can synchronously dispose its owning host.</summary>
    [Fact]
    public void Host_DragCompletionDisposesHost_DoesNotRefreshDisposedDispatcher()
    {
        var services = new VersionedHostServices();
        var root = new Canvas();
        var source = new Panel(Color.Red, 40f, 40f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            DragData = new UIDragData("panel"),
            AllowedDragEffects = UIDragEffect.Move
        };
        root.Add(source, Vector2.Zero);
        var host = new UIHost(services, services, services, root, 100f, 100f);
        source.Drag += (_, dragEvent) =>
        {
            if (dragEvent.Kind == UIDragEventKind.Cancel)
                host.Dispose();
        };

        services.RaiseVersionedPress(new Vector2(10f, 10f));
        services.RaiseVersionedMove(new Vector2(80f, 80f));
        var exception = Record.Exception(() =>
            services.RaiseVersionedRelease(new Vector2(80f, 80f)));

        Assert.Null(exception);
        host.Dispose();
    }

    /// <summary>Verifies runtime culture propagates and derives root text direction once.</summary>
    [Fact]
    public void Host_SetCulture_PropagatesCultureAndFlowDirection()
    {
        var services = new HostServices();
        var root = new Panel(Color.Black);
        var child = new Label("Status", 80f, 20f);
        root.AddChild(child);
        using var host = new UIHost(services, services, services, root, 320f, 200f);
        var submissions = services.SubmitCount;
        var culture = CultureInfo.GetCultureInfo("ar-SA");

        host.SetCulture(culture);

        Assert.Same(culture, host.Culture);
        Assert.Same(culture, child.Culture);
        Assert.Equal(UIFlowDirection.RightToLeft, root.FlowDirection);
        Assert.Equal(UIFlowDirection.RightToLeft, child.FlowDirection);
        Assert.Equal(submissions + 1, services.SubmitCount);
    }

    /// <summary>Verifies local culture and direction choices can remain independent of the host.</summary>
    [Fact]
    public void Host_SetCulture_PreservesExplicitDescendantOverrides()
    {
        var services = new HostServices();
        var root = new Panel(Color.Black);
        var child = new Label("Status", 80f, 20f)
        {
            Culture = CultureInfo.GetCultureInfo("en-US"),
            FlowDirection = UIFlowDirection.LeftToRight
        };
        root.AddChild(child);
        using var host = new UIHost(services, services, services, root, 320f, 200f);

        host.SetCulture(CultureInfo.GetCultureInfo("ar-SA"));

        Assert.Equal("en-US", child.Culture.Name);
        Assert.Equal(UIFlowDirection.LeftToRight, child.FlowDirection);
    }

    /// <summary>Verifies worker model notifications marshal target writes through the host dispatcher.</summary>
    [Fact]
    public void Binding_WorkerNotification_MarshalsToHostDispatcher()
    {
        var services = new HostServices();
        var model = new BindingModel("Initial");
        var label = new Label(string.Empty) { DataContext = model };
        using var host = new UIHost(services, services, services, label, 200f, 30f);
        using var binding = UIBinding.BindDataContext<BindingModel, string>(
            label, nameof(BindingModel.Value), source => source.Value,
            value => label.Text = value);
        Assert.Equal("Initial", label.Text);

        var worker = new Thread(() => model.Value = "Worker");
        worker.Start();
        worker.Join();
        Assert.Equal("Initial", label.Text);
        services.PumpFrame(0d);

        Assert.Equal("Worker", label.Text);
    }

    /// <summary>Verifies host disposal releases owned bindings before dispatcher detachment.</summary>
    [Fact]
    public void Binding_HostDisposal_DetachesObservableModel()
    {
        var services = new HostServices();
        var model = new BindingModel("Initial");
        var label = new Label(string.Empty) { DataContext = model };
        var host = new UIHost(services, services, services, label, 200f, 30f);
        _ = UIBinding.BindDataContext<BindingModel, string>(
            label, nameof(BindingModel.Value), source => source.Value,
            value => label.Text = value);

        host.Dispose();
        model.Value = "After disposal";

        Assert.Equal("Initial", label.Text);
    }

    /// <summary>Verifies the reusable pause layer centers actions and raises retained requests.</summary>
    [Fact]
    public void RuntimePauseMenu_OpenAndActions_UsesFullScreenModalLayer()
    {
        var services = new HostServices();
        var menu = new RuntimePauseMenu();
        using var host = new UIHost(services, services, services, menu, 800f, 600f);
        var resume = 0;
        var quit = 0;
        menu.ResumeRequested += () => resume++;
        menu.QuitRequested += () => quit++;

        menu.Open();
        host.Refresh();
        menu.ResumeButton.InvokeClick();
        menu.QuitButton.InvokeClick();

        Assert.True(menu.IsOpen);
        Assert.InRange(menu.ResumeButton.Left, 260f, 270f);
        Assert.InRange(menu.ResumeButton.Top, 230f, 290f);
        Assert.Equal(1, resume);
        Assert.Equal(1, quit);
        menu.Close();
        Assert.False(menu.IsOpen);
    }

    /// <summary>Minimal window, input, and renderer used to exercise UIHost boundaries.</summary>
    private class HostServices : IWindow, IInputSource, IPointerGestureSource, IRenderer,
        IInteractiveFrameScheduler
    {
        /// <summary>Gets the number of retained UI submissions.</summary>
        internal int SubmitCount { get; private set; }

        /// <summary>Gets the number of one-shot frame wake requests.</summary>
        internal int RequestFrameCount { get; private set; }

        /// <summary>Gets the most recently requested recurring-update state.</summary>
        internal bool ContinuousRendering { get; private set; }

        /// <summary>Gets the number of viewport presentation geometry updates.</summary>
        internal int ViewportQuadUpdateCount { get; private set; }

        /// <summary>Gets the latest viewport presentation geometry.</summary>
        internal VertexT[]? LastViewportQuad { get; private set; }

        /// <summary>Gets the number of render-view resize requests.</summary>
        internal int RenderViewResizeCount { get; private set; }

        /// <summary>Gets the logical size from the latest render-view resize.</summary>
        internal Vector2 LastRenderViewSize { get; private set; }

        /// <summary>Gets the number of immediate captured-interaction frame requests.</summary>
        internal int InteractiveFrameCount { get; private set; }

        /// <summary>Gets the latest native pointer cursor request.</summary>
        internal PointerCursorKind PointerCursor { get; private set; }

        /// <summary>Gets the number of renderer clear-color assignments.</summary>
        internal int UiClearColorSetCount { get; private set; }

        /// <inheritdoc/>
        public bool IsRunning => true;

        /// <inheritdoc/>
        public event Action<double>? Update;

        /// <inheritdoc/>
        public event Action<FrameProfileSample>? FrameProfiled { add { } remove { } }

        /// <inheritdoc/>
        public event Action<int, int>? Resized;

        /// <inheritdoc/>
        public event Action<Vector2>? MouseMove;

        /// <inheritdoc/>
        public event Action<int>? MouseDown;

        /// <inheritdoc/>
        public event Action<int>? MouseUp;

        /// <inheritdoc/>
        public event Action<int>? MouseDoubleClick { add { } remove { } }

        /// <inheritdoc/>
        public event Action<float>? MouseScroll { add { } remove { } }

        /// <inheritdoc/>
        public event Action<InputKey>? KeyDown { add { } remove { } }

        /// <inheritdoc/>
        public event Action<InputKey>? KeyUp { add { } remove { } }

        /// <inheritdoc/>
        public event Action<char>? TextInput { add { } remove { } }

        /// <inheritdoc/>
        public event Action<PointerMagnifyEvent>? PointerMagnified;

        /// <summary>Raises one logical resize.</summary>
        /// <param name="width">Logical width.</param>
        /// <param name="height">Logical height.</param>
        internal void RaiseResize(int width, int height) => Resized?.Invoke(width, height);

        /// <summary>Raises one native trackpad magnification event.</summary>
        /// <param name="pointerEvent">Gesture event to publish.</param>
        internal void RaiseMagnification(PointerMagnifyEvent pointerEvent) =>
            PointerMagnified?.Invoke(pointerEvent);

        /// <summary>Raises logical pointer movement.</summary>
        /// <param name="position">Logical pointer position.</param>
        internal void RaiseMove(Vector2 position) => MouseMove?.Invoke(position);

        /// <summary>Raises a complete primary-button click at a position.</summary>
        /// <param name="position">Logical pointer position.</param>
        internal void RaiseClick(Vector2 position)
        {
            MouseMove?.Invoke(position);
            MouseDown?.Invoke(0);
            MouseUp?.Invoke(0);
        }

        /// <summary>Raises a primary-button press at a position.</summary>
        /// <param name="position">Logical pointer position.</param>
        internal void RaisePress(Vector2 position)
        {
            MouseMove?.Invoke(position);
            MouseDown?.Invoke(0);
        }

        /// <summary>Raises a primary-button release.</summary>
        internal void RaiseRelease() => MouseUp?.Invoke(0);

        /// <summary>Raises one update with a controlled elapsed duration.</summary>
        /// <param name="deltaTime">Elapsed seconds.</param>
        internal void PumpFrame(double deltaTime) => Update?.Invoke(deltaTime);

        /// <inheritdoc/>
        public void Initialize(WindowOptions options) { }

        /// <inheritdoc/>
        public void Run() { }

        /// <inheritdoc/>
        public void Shutdown() { }

        /// <inheritdoc/>
        public void ProcessEvents() { }

        /// <inheritdoc/>
        public void PumpFrame() => Update?.Invoke(0d);

        /// <inheritdoc/>
        public void RequestFrame() => RequestFrameCount++;

        /// <inheritdoc/>
        public void SetContinuousRendering(bool enabled) => ContinuousRendering = enabled;

        /// <inheritdoc/>
        public void PresentInteractiveFrame() => InteractiveFrameCount++;

        /// <inheritdoc/>
        public void BeginWindowDrag(Vector2 pointerPosition) { }

        /// <inheritdoc/>
        public void UpdateWindowDrag(Vector2 pointerPosition) { }

        /// <inheritdoc/>
        public void EndWindowDrag() { }

        /// <inheritdoc/>
        public void Minimize() { }

        /// <inheritdoc/>
        public void ToggleMaximize() { }

        /// <inheritdoc/>
        public void ToggleFullScreen() { }

        /// <inheritdoc/>
        public void Close() { }

        /// <inheritdoc/>
        public void SetMouseCaptured(bool captured) { }

        /// <inheritdoc/>
        public void SetPointerCursor(PointerCursorKind kind)
        {
            PointerCursor = kind;
        }

        /// <inheritdoc/>
        public MeshHandle CreateMesh(MeshDescription description) => default;

        /// <inheritdoc/>
        public MeshHandle CreateStaticMesh(
            StaticMeshResource mesh,
            StandardMaterialResource material) => default;

        /// <inheritdoc/>
        public SkinnedMeshHandles CreateSkinnedMesh(
            SkinnedMeshResource mesh,
            StandardMaterialResource material) => default;

        /// <inheritdoc/>
        public void UpdateSkinPalette(
            SkinPaletteHandle palette,
            ReadOnlySpan<Matrix4x4> matrices) { }

        /// <inheritdoc/>
        public void DestroySkinPalette(SkinPaletteHandle palette) { }

        /// <inheritdoc/>
        public TextureHandle CreateTexture(TextureResource texture) => default;

        /// <inheritdoc/>
        public void DestroyTexture(TextureHandle texture) { }

        /// <inheritdoc/>
        public void UpdateMesh(MeshHandle mesh, MeshUpdate update) { }

        /// <inheritdoc/>
        public void DestroyMesh(MeshHandle mesh) { }

        /// <inheritdoc/>
        public void SubmitUI(UIDrawList drawList) => SubmitCount++;

        /// <inheritdoc/>
        public void SetPushConstants(PushConstants pushConstants) { }

        /// <inheritdoc/>
        public void SetUiClearColor(float r, float g, float b, float a = 1f) =>
            UiClearColorSetCount++;

        /// <inheritdoc/>
        public RenderViewHandle CreateRenderView(float width, float height) => default;

        /// <inheritdoc/>
        public void DestroyRenderView(RenderViewHandle view) { }

        /// <inheritdoc/>
        public void ResizeRenderView(RenderViewHandle view, float width, float height)
        {
            RenderViewResizeCount++;
            LastRenderViewSize = new Vector2(width, height);
        }

        /// <inheritdoc/>
        public void SetViewportQuadVertices(RenderViewHandle view, VertexT[] vertices)
        {
            ViewportQuadUpdateCount++;
            LastViewportQuad = vertices;
        }

        /// <inheritdoc/>
        public ViewportRenderContext CreateRenderContext(RenderViewHandle view) => new();

        /// <inheritdoc/>
        public void Submit(RenderViewHandle view, RenderQueue renderQueue) { }

        /// <inheritdoc/>
        public void DrawGroundGrid(
            RenderViewHandle renderView,
            Matrix4x4 view,
            Matrix4x4 projection) { }

        /// <inheritdoc/>
        public void SetViewportClearColor(
            RenderViewHandle view,
            float r,
            float g,
            float b,
            float a = 1f) { }

        /// <inheritdoc/>
        public void SubmitTransient(TransientGeometry geometry) { }

        /// <inheritdoc/>
        public void Dispose() { }
    }

    /// <summary>Test host that emits device-neutral controller navigation.</summary>
    private sealed class NavigationHostServices : HostServices, INavigationInputSource
    {
        /// <inheritdoc/>
        public event Action<NavigationInputEvent>? NavigationChanged;

        /// <summary>Raises one pressed controller navigation action.</summary>
        /// <param name="action">Logical navigation action.</param>
        internal void RaiseNavigation(UINavigationAction action) =>
            NavigationChanged?.Invoke(new NavigationInputEvent(action, true));

        /// <summary>Raises one released controller navigation action.</summary>
        /// <param name="action">Logical navigation action.</param>
        internal void ReleaseNavigation(UINavigationAction action) =>
            NavigationChanged?.Invoke(new NavigationInputEvent(action, false));
    }

    /// <summary>Records the delta supplied by its inherited UI clock.</summary>
    private sealed class ClockProbe : UIElement
    {
        /// <summary>Gets accumulated clock seconds.</summary>
        internal double Elapsed { get; private set; }

        /// <inheritdoc/>
        protected override bool IsTimeUpdateActive => true;

        /// <inheritdoc/>
        protected override bool UpdateElement(double deltaTime)
        {
            Elapsed += deltaTime;
            return false;
        }
    }

    /// <summary>Observable model used to verify dispatcher-aware bindings.</summary>
    private sealed class BindingModel : INotifyPropertyChanged
    {
        private string _value;

        /// <summary>Creates a model with an initial value.</summary>
        /// <param name="value">Initial value.</param>
        public BindingModel(string value) => _value = value;

        /// <summary>Gets or sets the observable value.</summary>
        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                    return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        /// <inheritdoc/>
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Deterministic text service used to verify host service inheritance.</summary>
    private sealed class FixedTextLayoutService : ITextLayoutService
    {
        /// <summary>Gets the number of caret hit tests.</summary>
        internal int HitTestCount { get; private set; }

        /// <inheritdoc/>
        public float MeasureWidth(ReadOnlySpan<char> text, float fontSize) => text.Length * 11f;

        /// <inheritdoc/>
        public int HitTestCaret(
            ReadOnlySpan<char> text,
            float fontSize,
            float horizontalPosition)
        {
            HitTestCount++;
            return Math.Min(3, text.Length);
        }
    }

    /// <summary>Host services exposing the versioned input contract.</summary>
    private sealed class VersionedHostServices : HostServices, IInputSourceV2, ITextInputMethodSource
    {
        /// <inheritdoc/>
        public event Action<PointerMoveEvent>? PointerMoved;

        /// <inheritdoc/>
        public event Action<PointerButtonEvent>? PointerButtonChanged;

        /// <inheritdoc/>
        public event Action<PointerWheelEvent>? PointerWheelChanged;

        /// <inheritdoc/>
        public event Action<KeyInputEvent>? KeyChanged;

        /// <inheritdoc/>
        public event Action<string>? TextEntered;

        /// <inheritdoc/>
        public event Action<TextCompositionEvent>? TextCompositionChanged;

        /// <summary>Raises a complete versioned primary-button click.</summary>
        /// <param name="position">Logical click position.</param>
        internal void RaiseVersionedClick(Vector2 position)
        {
            PointerMoved?.Invoke(new PointerMoveEvent(
                0, position, Vector2.Zero, PointerDeviceKind.Mouse,
                InputModifiers.None, PointerButtons.None));
            PointerButtonChanged?.Invoke(new PointerButtonEvent(
                0, position, InputPointerButton.Primary, true, 1,
                PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));
            PointerButtonChanged?.Invoke(new PointerButtonEvent(
                0, position, InputPointerButton.Primary, false, 1,
                PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.None));
        }

        /// <summary>Raises a versioned pointer move.</summary>
        /// <param name="position">Logical pointer position.</param>
        internal void RaiseVersionedMove(Vector2 position) =>
            PointerMoved?.Invoke(new PointerMoveEvent(
                0, position, Vector2.Zero, PointerDeviceKind.Mouse,
                InputModifiers.None, PointerButtons.Primary));

        /// <summary>Raises one versioned pointer-wheel delta.</summary>
        /// <param name="position">Logical pointer position.</param>
        /// <param name="delta">Fine-grained wheel movement.</param>
        internal void RaiseWheel(Vector2 position, Vector2 delta) =>
            PointerWheelChanged?.Invoke(new PointerWheelEvent(
                0, position, delta, InputModifiers.None));

        /// <summary>Raises a versioned primary-button press.</summary>
        /// <param name="position">Logical pointer position.</param>
        internal void RaiseVersionedPress(Vector2 position)
        {
            PointerMoved?.Invoke(new PointerMoveEvent(
                0, position, Vector2.Zero, PointerDeviceKind.Mouse,
                InputModifiers.None, PointerButtons.None));
            PointerButtonChanged?.Invoke(new PointerButtonEvent(
                0, position, InputPointerButton.Primary, true, 1,
                PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));
        }

        /// <summary>Raises a versioned primary-button release.</summary>
        /// <param name="position">Logical pointer position.</param>
        internal void RaiseVersionedRelease(Vector2 position) =>
            PointerButtonChanged?.Invoke(new PointerButtonEvent(
                0, position, InputPointerButton.Primary, false, 1,
                PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.None));

        /// <summary>Raises one versioned keyboard transition.</summary>
        /// <param name="keyEvent">Transition to publish.</param>
        internal void RaiseKey(KeyInputEvent keyEvent) => KeyChanged?.Invoke(keyEvent);

        /// <summary>Raises committed versioned text.</summary>
        /// <param name="text">Committed Unicode text.</param>
        internal void RaiseText(string text) => TextEntered?.Invoke(text);

        /// <summary>Raises one input-method composition transition.</summary>
        /// <param name="composition">Composition transition.</param>
        internal void RaiseComposition(TextCompositionEvent composition) =>
            TextCompositionChanged?.Invoke(composition);
    }
}
