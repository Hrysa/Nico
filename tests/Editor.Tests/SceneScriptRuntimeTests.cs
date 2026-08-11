using Engine.Graphics;
using Engine.Core;
using Engine.Scripting;
using Xunit;

namespace Editor.Tests;

public class SceneScriptRuntimeTests
{
    /// <summary>Verifies attached scripts receive their owner, scene, and lifecycle callbacks.</summary>
    [Fact]
    public void Runtime_AttachedScript_ReceivesLifecycle()
    {
        var root = new Node3D { Name = "Scene" };
        var owner = new Node3D
        {
            Name = "Target",
            ScriptId = AssetId.New()
        };
        root.AddChild(owner);
        using var runtime = new SceneScriptRuntime();

        runtime.Attach(root, new TestScriptCatalog(owner.ScriptId.Value, typeof(RecordingScript)));
        runtime.Start();
        runtime.Update(0.25);
        runtime.LateUpdate(0.25);

        var script = Assert.IsType<RecordingScript>(Assert.Single(runtime.Scripts));
        Assert.Same(owner, script.Owner);
        Assert.Same(owner, script.Scene.FindNode("Target"));
        Assert.True(script.ReadyCalled);
        Assert.Equal(0.25, script.LastDeltaTime);
        Assert.True(script.LateUpdateCalled);
    }

    /// <summary>Verifies multiple script components run and authored values use generated accessors.</summary>
    [Fact]
    public void Runtime_MultipleScriptComponents_AppliesOverridesAndEnabledState()
    {
        var scriptId = AssetId.New();
        var root = new Node3D { Name = "Scene" };
        var owner = new Node3D { Name = "Target" };
        var enabled = new ScriptComponent(scriptId);
        enabled.SetPropertyOverride(RecordingObservedScript.SpeedId,
            SerializedPropertyValue.From(3d));
        var disabled = new ScriptComponent(scriptId) { Enabled = false };
        disabled.SetPropertyOverride(RecordingObservedScript.SpeedId,
            SerializedPropertyValue.From(7d));
        owner.AddComponent(enabled);
        owner.AddComponent(disabled);
        root.AddChild(owner);
        using var runtime = new SceneScriptRuntime();

        runtime.Attach(root, new TestScriptCatalog(scriptId, typeof(RecordingObservedScript)));
        runtime.Start();
        runtime.Update(0.5d);

        Assert.Equal(2, runtime.Scripts.Count);
        var enabledScript = Assert.IsType<RecordingObservedScript>(runtime.Scripts[0]);
        var disabledScript = Assert.IsType<RecordingObservedScript>(runtime.Scripts[1]);
        Assert.Same(enabled, enabledScript.Component);
        Assert.Equal(3d, enabledScript.Speed);
        Assert.Equal(7d, disabledScript.Speed);
        Assert.Equal(1, enabledScript.UpdateCount);
        Assert.Equal(0, disabledScript.UpdateCount);
    }

    /// <summary>Verifies component filtering and script enumeration allocate nothing per update.</summary>
    [Fact]
    public void Runtime_Update_AfterWarmup_DoesNotAllocate()
    {
        var scriptId = AssetId.New();
        var root = new Node3D();
        var owner = new Node3D();
        owner.AddComponent(new ScriptComponent(scriptId));
        root.AddChild(owner);
        using var runtime = new SceneScriptRuntime();
        runtime.Attach(root, new TestScriptCatalog(scriptId, typeof(RecordingObservedScript)));
        runtime.Start();
        runtime.Update(1d / 60d);

        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
            runtime.Update(1d / 60d);
        var allocationEnd = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(allocationStart, allocationEnd);
    }

    /// <summary>Verifies scene scripts receive held and one-update keyboard transitions.</summary>
    [Fact]
    public void Runtime_InputSource_ProvidesStableKeyboardState()
    {
        var scriptId = AssetId.New();
        var root = new Node3D();
        var owner = new Node3D();
        owner.AddComponent(new ScriptComponent(scriptId));
        root.AddChild(owner);
        var input = new TestInputSource();
        using var runtime = new SceneScriptRuntime();
        runtime.Attach(root, new TestScriptCatalog(scriptId, typeof(InputRecordingScript)), input);
        runtime.Start();
        var script = Assert.IsType<InputRecordingScript>(Assert.Single(runtime.Scripts));

        input.Press(InputKey.W);
        runtime.Update(1d / 60d);
        Assert.True(script.Held);
        Assert.True(script.Pressed);
        Assert.False(script.Released);

        runtime.Update(1d / 60d);
        Assert.True(script.Held);
        Assert.False(script.Pressed);

        input.Release(InputKey.W);
        runtime.Update(1d / 60d);
        Assert.False(script.Held);
        Assert.True(script.Released);
    }

    /// <summary>Verifies scene scripts receive accumulated frame pointer movement and held buttons.</summary>
    [Fact]
    public void Runtime_InputSource_ProvidesStablePointerState()
    {
        var scriptId = AssetId.New();
        var root = new Node3D();
        var owner = new Node3D();
        owner.AddComponent(new ScriptComponent(scriptId));
        root.AddChild(owner);
        var input = new TestInputSource();
        using var runtime = new SceneScriptRuntime();
        runtime.Attach(root, new TestScriptCatalog(scriptId, typeof(InputRecordingScript)), input);
        runtime.Start();
        var script = Assert.IsType<InputRecordingScript>(Assert.Single(runtime.Scripts));

        input.SetPointerButton(InputPointerButton.Secondary, true);
        input.MovePointer(new System.Numerics.Vector2(10f, 20f), new System.Numerics.Vector2(3f, -2f));
        input.MovePointer(new System.Numerics.Vector2(14f, 25f), new System.Numerics.Vector2(4f, 5f));
        runtime.Update(1d / 60d);

        Assert.True(script.SecondaryPointerHeld);
        Assert.True(input.MouseCaptured);
        Assert.Equal(new System.Numerics.Vector2(14f, 25f), script.PointerPosition);
        Assert.Equal(new System.Numerics.Vector2(7f, 3f), script.PointerDelta);

        runtime.Update(1d / 60d);
        Assert.True(script.SecondaryPointerHeld);
        Assert.Equal(System.Numerics.Vector2.Zero, script.PointerDelta);

        input.SetPointerButton(InputPointerButton.Secondary, false);
        runtime.Update(1d / 60d);
        Assert.False(script.SecondaryPointerHeld);
        Assert.False(input.MouseCaptured);
    }

    /// <summary>Verifies scripts can directly play a controller supplied by the active scene.</summary>
    [Fact]
    public void Runtime_AnimationService_ProvidesOwnerControllerBeforeReady()
    {
        var scriptId = AssetId.New();
        var root = new Node3D();
        var owner = new Node3D { Name = "Animated" };
        owner.AddComponent(new ScriptComponent(scriptId));
        root.AddChild(owner);
        var skeleton = new SkeletonResource([]);
        var clip = new AnimationClipResource("Idle", 1f, []);
        var resource = new SkinnedMeshResource(
            new StaticMeshResource([], [], []), [], skeleton, [clip]);
        using var animations = new SceneAnimationRegistry();
        var controller = new AnimationController(resource);
        animations.Register(owner, controller);
        using var runtime = new SceneScriptRuntime();

        runtime.Attach(root,
            new TestScriptCatalog(scriptId, typeof(AnimationRecordingScript)),
            animationService: animations);
        runtime.Start();

        var script = Assert.IsType<AnimationRecordingScript>(Assert.Single(runtime.Scripts));
        Assert.Same(controller, script.Controller);
        Assert.Equal("Idle", controller.Current?.Key);
    }

    /// <summary>Provides the active game pipeline service before script readiness.</summary>
    [Fact]
    public void Runtime_RenderingService_CanBeConfiguredByScript()
    {
        var scriptId = AssetId.New();
        var root = new Node3D();
        var owner = new Node3D();
        owner.AddComponent(new ScriptComponent(scriptId));
        root.AddChild(owner);
        var rendering = new TestRenderingService();
        using var runtime = new SceneScriptRuntime();

        runtime.Attach(root,
            new TestScriptCatalog(scriptId, typeof(RenderingRecordingScript)),
            renderingService: rendering);
        runtime.Start();

        var script = Assert.IsType<RenderingRecordingScript>(Assert.Single(runtime.Scripts));
        Assert.Same(rendering, script.Rendering);
        Assert.NotSame(BasicForwardRenderPipeline.Instance, rendering.RenderPipeline);
    }

    private sealed class TestScriptCatalog(AssetId id, Type type) : IScriptTypeCatalog
    {
        /// <inheritdoc />
        public bool TryResolve(AssetId asset, out Type? scriptType)
        {
            scriptType = asset == id ? type : null;
            return scriptType is not null;
        }
    }

    /// <summary>Records lifecycle activity for a runtime test.</summary>
    public sealed class RecordingScript : SceneScript
    {
        /// <summary>Gets whether ready was called.</summary>
        public bool ReadyCalled { get; private set; }

        /// <summary>Gets the most recent update delta.</summary>
        public double LastDeltaTime { get; private set; }

        /// <summary>Gets whether the post-physics update was called.</summary>
        public bool LateUpdateCalled { get; private set; }

        /// <inheritdoc />
        public override void OnReady()
        {
            ReadyCalled = true;
        }

        /// <inheritdoc />
        public override void OnUpdate(double deltaTime)
        {
            LastDeltaTime = deltaTime;
            Owner.Rotation += new System.Numerics.Vector3(0f, (float)deltaTime, 0f);
        }

        /// <inheritdoc />
        public override void OnLateUpdate(double deltaTime)
        {
            LateUpdateCalled = true;
        }
    }

    /// <summary>Provides a hand-authored equivalent of generated access for runtime integration.</summary>
    public sealed class RecordingObservedScript : SceneScript
    {
        /// <summary>Stable property identifier used by the test contract.</summary>
        public const int SpeedId = 8172;

        /// <summary>Gets or sets the observed speed.</summary>
        public double Speed { get; set; }

        /// <summary>Gets the number of enabled updates.</summary>
        public int UpdateCount { get; private set; }

        /// <inheritdoc/>
        public override bool TrySetObservedValue(int propertyId, ObservedValue value)
        {
            if (propertyId != SpeedId || !value.TryGetNumber(out var speed))
                return false;
            Speed = speed;
            return true;
        }

        /// <inheritdoc/>
        public override void OnUpdate(double deltaTime)
        {
            UpdateCount++;
        }
    }

    /// <summary>Records input state observed during its most recent update.</summary>
    public sealed class InputRecordingScript : SceneScript
    {
        /// <summary>Gets whether W was held.</summary>
        public bool Held { get; private set; }

        /// <summary>Gets whether W was newly pressed.</summary>
        public bool Pressed { get; private set; }

        /// <summary>Gets whether W was newly released.</summary>
        public bool Released { get; private set; }

        /// <summary>Gets whether the secondary pointer button was held.</summary>
        public bool SecondaryPointerHeld { get; private set; }

        /// <summary>Gets the most recent logical pointer position.</summary>
        public System.Numerics.Vector2 PointerPosition { get; private set; }

        /// <summary>Gets accumulated pointer movement for the most recent update.</summary>
        public System.Numerics.Vector2 PointerDelta { get; private set; }

        /// <inheritdoc/>
        public override void OnUpdate(double deltaTime)
        {
            Held = Scene.Input.IsKeyDown(InputKey.W);
            Pressed = Scene.Input.WasKeyPressed(InputKey.W);
            Released = Scene.Input.WasKeyReleased(InputKey.W);
            SecondaryPointerHeld = Scene.Input.IsPointerButtonDown(InputPointerButton.Secondary);
            Scene.Input.SetPointerCaptured(SecondaryPointerHeld);
            PointerPosition = Scene.Input.PointerPosition;
            PointerDelta = Scene.Input.PointerDelta;
        }
    }

    /// <summary>Obtains and starts its owner's animation controller during readiness.</summary>
    public sealed class AnimationRecordingScript : SceneScript
    {
        /// <summary>Gets the controller resolved during readiness.</summary>
        public AnimationController? Controller { get; private set; }

        /// <inheritdoc/>
        public override void OnReady()
        {
            Controller = Scene.Animation.GetRequired(Owner);
            Controller.Play("Idle");
        }
    }

    /// <summary>Replaces the pipeline exposed during readiness.</summary>
    public sealed class RenderingRecordingScript : SceneScript
    {
        /// <summary>Gets the rendering service observed during readiness.</summary>
        public ISceneRenderingService? Rendering { get; private set; }

        /// <inheritdoc/>
        public override void OnReady()
        {
            Rendering = Scene.Rendering;
            Rendering.RenderPipeline = new RenderPipeline(new ForwardOpaqueRenderPass());
        }
    }

    private sealed class TestRenderingService : ISceneRenderingService
    {
        /// <inheritdoc/>
        public RenderPipeline RenderPipeline { get; set; } =
            BasicForwardRenderPipeline.Instance;
    }

    /// <summary>Raises device-neutral input events for runtime tests.</summary>
    private sealed class TestInputSource : IInputSourceV2
    {
        /// <summary>Gets the latest requested pointer capture state.</summary>
        public bool MouseCaptured { get; private set; }

        public event Action<KeyInputEvent>? KeyChanged;

#pragma warning disable CS0067
        public event Action<System.Numerics.Vector2>? MouseMove;
        public event Action<int>? MouseDown;
        public event Action<int>? MouseUp;
        public event Action<int>? MouseDoubleClick;
        public event Action<float>? MouseScroll;
        public event Action<InputKey>? KeyDown;
        public event Action<InputKey>? KeyUp;
        public event Action<char>? TextInput;
        public event Action<PointerMoveEvent>? PointerMoved;
        public event Action<PointerButtonEvent>? PointerButtonChanged;
        public event Action<PointerWheelEvent>? PointerWheelChanged;
        public event Action<string>? TextEntered;
#pragma warning restore CS0067

        /// <summary>Raises an initial key press.</summary>
        /// <param name="key">Pressed key.</param>
        public void Press(InputKey key) =>
            KeyChanged?.Invoke(new KeyInputEvent(key, true, false, InputModifiers.None));

        /// <summary>Raises a key release.</summary>
        /// <param name="key">Released key.</param>
        public void Release(InputKey key) =>
            KeyChanged?.Invoke(new KeyInputEvent(key, false, false, InputModifiers.None));

        /// <summary>Raises one device-neutral pointer movement.</summary>
        /// <param name="position">New logical pointer position.</param>
        /// <param name="delta">Movement since the preceding event.</param>
        public void MovePointer(System.Numerics.Vector2 position, System.Numerics.Vector2 delta) =>
            PointerMoved?.Invoke(new PointerMoveEvent(
                0,
                position,
                delta,
                PointerDeviceKind.Mouse,
                InputModifiers.None,
                PointerButtons.None));

        /// <summary>Raises one device-neutral pointer-button transition.</summary>
        /// <param name="button">Changed button.</param>
        /// <param name="isPressed">Whether the button became pressed.</param>
        public void SetPointerButton(InputPointerButton button, bool isPressed) =>
            PointerButtonChanged?.Invoke(new PointerButtonEvent(
                0,
                System.Numerics.Vector2.Zero,
                button,
                isPressed,
                1,
                PointerDeviceKind.Mouse,
                InputModifiers.None,
                isPressed && button == InputPointerButton.Secondary
                    ? PointerButtons.Secondary : PointerButtons.None));

        /// <inheritdoc/>
        public void SetMouseCaptured(bool captured)
        {
            MouseCaptured = captured;
        }
    }
}
