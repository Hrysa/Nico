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

        var script = Assert.IsType<RecordingScript>(Assert.Single(runtime.Scripts));
        Assert.Same(owner, script.Owner);
        Assert.Same(owner, script.Scene.FindNode("Target"));
        Assert.True(script.ReadyCalled);
        Assert.Equal(0.25, script.LastDeltaTime);
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
    }
}
