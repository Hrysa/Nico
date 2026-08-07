using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>
/// Owns Vulkan shader, pipeline, layout, and texture descriptor handles.
/// </summary>
internal unsafe sealed class PipelineResources : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private bool _disposed;

    internal ShaderModule UiVertexShader;
    internal ShaderModule UiFragmentShader;
    internal PipelineLayout UiLayout;
    internal Pipeline UiPipeline;
    internal ShaderModule UiShapeVertexShader;
    internal ShaderModule UiShapeFragmentShader;
    internal Pipeline UiShapePipeline;
    internal Pipeline ViewportPipeline;
    internal PipelineLayout ViewportLayout;
    internal ShaderModule ModelVertexShader;
    internal ShaderModule ModelFragmentShader;
    internal Pipeline ModelPipeline;
    internal PipelineLayout ModelLayout;
    internal DescriptorSetLayout ModelTextureDescriptorSetLayout;
    internal DescriptorPool ModelTextureDescriptorPool;
    internal ShaderModule GridVertexShader;
    internal ShaderModule GridFragmentShader;
    internal PipelineLayout GridLayout;
    internal Pipeline GridPipeline;
    internal DescriptorSetLayout TextureDescriptorSetLayout;
    internal DescriptorPool TextureDescriptorPool;
    internal ShaderModule TextureVertexShader;
    internal ShaderModule TextureFragmentShader;
    internal PipelineLayout TextureLayout;
    internal Pipeline TexturePipeline;

    /// <summary>
    /// Creates a pipeline resource owner for one logical device.
    /// </summary>
    /// <param name="vk">Vulkan API instance.</param>
    /// <param name="device">Logical device that owns the resources.</param>
    internal PipelineResources(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    /// <summary>
    /// Releases all owned Vulkan handles in dependency-safe order.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DestroyPipeline(TexturePipeline);
        DestroyPipeline(GridPipeline);
        DestroyPipeline(ModelPipeline);
        DestroyPipeline(ViewportPipeline);
        DestroyPipeline(UiShapePipeline);
        DestroyPipeline(UiPipeline);
        DestroyPipelineLayout(TextureLayout);
        DestroyPipelineLayout(GridLayout);
        DestroyPipelineLayout(ModelLayout);
        DestroyPipelineLayout(ViewportLayout);
        DestroyPipelineLayout(UiLayout);

        if (TextureDescriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, TextureDescriptorPool, null);
        if (ModelTextureDescriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, ModelTextureDescriptorPool, null);
        if (TextureDescriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, TextureDescriptorSetLayout, null);
        if (ModelTextureDescriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, ModelTextureDescriptorSetLayout, null);

        DestroyShaderModule(TextureVertexShader);
        DestroyShaderModule(TextureFragmentShader);
        DestroyShaderModule(GridVertexShader);
        DestroyShaderModule(GridFragmentShader);
        DestroyShaderModule(ModelVertexShader);
        DestroyShaderModule(ModelFragmentShader);
        DestroyShaderModule(UiVertexShader);
        DestroyShaderModule(UiFragmentShader);
        DestroyShaderModule(UiShapeVertexShader);
        DestroyShaderModule(UiShapeFragmentShader);
    }

    /// <summary>
    /// Destroys a pipeline when it has been created.
    /// </summary>
    /// <param name="pipeline">Pipeline handle.</param>
    private void DestroyPipeline(Pipeline pipeline)
    {
        if (pipeline.Handle != 0)
            _vk.DestroyPipeline(_device, pipeline, null);
    }

    /// <summary>
    /// Destroys a pipeline layout when it has been created.
    /// </summary>
    /// <param name="layout">Pipeline-layout handle.</param>
    private void DestroyPipelineLayout(PipelineLayout layout)
    {
        if (layout.Handle != 0)
            _vk.DestroyPipelineLayout(_device, layout, null);
    }

    /// <summary>
    /// Destroys a shader module when it has been created.
    /// </summary>
    /// <param name="shaderModule">Shader-module handle.</param>
    private void DestroyShaderModule(ShaderModule shaderModule)
    {
        if (shaderModule.Handle != 0)
            _vk.DestroyShaderModule(_device, shaderModule, null);
    }
}
