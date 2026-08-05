using System.Numerics;
using Editor;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIHostTests
{
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

    /// <summary>Minimal window, input, and renderer used to exercise UIHost boundaries.</summary>
    private sealed class HostServices : IWindow, IInputSource, IRenderer
    {
        /// <summary>Gets the number of retained UI submissions.</summary>
        internal int SubmitCount { get; private set; }

        /// <inheritdoc/>
        public bool IsRunning => true;

        /// <inheritdoc/>
        public event Action<double>? Update;

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

        /// <summary>Raises one logical resize.</summary>
        /// <param name="width">Logical width.</param>
        /// <param name="height">Logical height.</param>
        internal void RaiseResize(int width, int height) => Resized?.Invoke(width, height);

        /// <summary>Raises a complete primary-button click at a position.</summary>
        /// <param name="position">Logical pointer position.</param>
        internal void RaiseClick(Vector2 position)
        {
            MouseMove?.Invoke(position);
            MouseDown?.Invoke(0);
            MouseUp?.Invoke(0);
        }

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
        public void RequestFrame() { }

        /// <inheritdoc/>
        public void SetContinuousRendering(bool enabled) { }

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
        public MeshHandle CreateMesh(MeshDescription description) => default;

        /// <inheritdoc/>
        public MeshHandle CreateStaticMesh(
            StaticMeshResource mesh,
            StandardMaterialResource material) => default;

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
        public RenderViewHandle CreateRenderView(float width, float height) => default;

        /// <inheritdoc/>
        public void DestroyRenderView(RenderViewHandle view) { }

        /// <inheritdoc/>
        public void ResizeRenderView(RenderViewHandle view, float width, float height) { }

        /// <inheritdoc/>
        public void SetViewportQuadVertices(RenderViewHandle view, VertexT[] vertices) { }

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
}
