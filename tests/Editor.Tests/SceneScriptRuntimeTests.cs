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

        /// <inheritdoc/>
        public override void OnUpdate(double deltaTime)
        {
            Held = Scene.Input.IsKeyDown(InputKey.W);
            Pressed = Scene.Input.WasKeyPressed(InputKey.W);
            Released = Scene.Input.WasKeyReleased(InputKey.W);
        }
    }

    /// <summary>Raises device-neutral input events for runtime tests.</summary>
    private sealed class TestInputSource : IInputSourceV2
    {
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

        /// <inheritdoc/>
        public void SetMouseCaptured(bool captured)
        {
        }
    }
}
