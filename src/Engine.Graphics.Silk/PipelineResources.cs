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
    internal Pipeline ViewportTransparentPipeline;
    internal PipelineLayout ViewportLayout;
    internal ShaderModule ModelVertexShader;
    internal ShaderModule ModelFragmentShader;
    internal Pipeline ModelPipeline;
    internal Pipeline ModelDoubleSidedPipeline;
    internal Pipeline ModelTransparentPipeline;
    internal Pipeline ModelTransparentDoubleSidedPipeline;
    internal PipelineLayout ModelLayout;
    internal DescriptorSetLayout ModelTextureDescriptorSetLayout;
    internal DescriptorPool ModelTextureDescriptorPool;
    internal DescriptorSetLayout ShadowSamplingDescriptorSetLayout;
    internal DescriptorPool ShadowSamplingDescriptorPool;
    internal DescriptorSetLayout SceneLightingDescriptorSetLayout;
    internal DescriptorPool SceneLightingDescriptorPool;
    internal ShaderModule SkinnedModelVertexShader;
    internal ShaderModule SkinnedModelFragmentShader;
    internal Pipeline SkinnedModelPipeline;
    internal Pipeline SkinnedModelDoubleSidedPipeline;
    internal Pipeline SkinnedModelTransparentPipeline;
    internal Pipeline SkinnedModelTransparentDoubleSidedPipeline;
    internal PipelineLayout SkinnedModelLayout;
    internal DescriptorSetLayout SkinPaletteDescriptorSetLayout;
    internal DescriptorPool SkinPaletteDescriptorPool;
    internal ShaderModule ShadowVertexShader;
    internal ShaderModule SkinnedShadowVertexShader;
    internal Pipeline ShadowPipeline;
    internal Pipeline SkinnedShadowPipeline;
    internal Pipeline CameraDepthPipeline;
    internal Pipeline SkinnedCameraDepthPipeline;
    internal PipelineLayout ShadowLayout;
    internal PipelineLayout SkinnedShadowLayout;
    internal ShaderModule GridVertexShader;
    internal ShaderModule GridFragmentShader;
    internal PipelineLayout GridLayout;
    internal Pipeline GridPipeline;
    internal ShaderModule SkyboxVertexShader;
    internal ShaderModule SkyboxFragmentShader;
    internal PipelineLayout SkyboxLayout;
    internal Pipeline SkyboxPipeline;
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
        DestroyPipeline(SkyboxPipeline);
        DestroyPipeline(GridPipeline);
        DestroyPipeline(ModelPipeline);
        DestroyPipeline(ModelDoubleSidedPipeline);
        DestroyPipeline(ModelTransparentPipeline);
        DestroyPipeline(ModelTransparentDoubleSidedPipeline);
        DestroyPipeline(SkinnedModelPipeline);
        DestroyPipeline(SkinnedModelDoubleSidedPipeline);
        DestroyPipeline(SkinnedModelTransparentPipeline);
        DestroyPipeline(SkinnedModelTransparentDoubleSidedPipeline);
        DestroyPipeline(ShadowPipeline);
        DestroyPipeline(SkinnedShadowPipeline);
        DestroyPipeline(CameraDepthPipeline);
        DestroyPipeline(SkinnedCameraDepthPipeline);
        DestroyPipeline(ViewportPipeline);
        DestroyPipeline(ViewportTransparentPipeline);
        DestroyPipeline(UiShapePipeline);
        DestroyPipeline(UiPipeline);
        DestroyPipelineLayout(TextureLayout);
        DestroyPipelineLayout(SkyboxLayout);
        DestroyPipelineLayout(GridLayout);
        DestroyPipelineLayout(ModelLayout);
        DestroyPipelineLayout(SkinnedModelLayout);
        DestroyPipelineLayout(ShadowLayout);
        DestroyPipelineLayout(SkinnedShadowLayout);
        DestroyPipelineLayout(ViewportLayout);
        DestroyPipelineLayout(UiLayout);

        if (TextureDescriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, TextureDescriptorPool, null);
        if (ModelTextureDescriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, ModelTextureDescriptorPool, null);
        if (ShadowSamplingDescriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, ShadowSamplingDescriptorPool, null);
        if (SceneLightingDescriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, SceneLightingDescriptorPool, null);
        if (SkinPaletteDescriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, SkinPaletteDescriptorPool, null);
        if (TextureDescriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, TextureDescriptorSetLayout, null);
        if (ModelTextureDescriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, ModelTextureDescriptorSetLayout, null);
        if (ShadowSamplingDescriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, ShadowSamplingDescriptorSetLayout, null);
        if (SceneLightingDescriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, SceneLightingDescriptorSetLayout, null);
        if (SkinPaletteDescriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, SkinPaletteDescriptorSetLayout, null);

        DestroyShaderModule(TextureVertexShader);
        DestroyShaderModule(TextureFragmentShader);
        DestroyShaderModule(GridVertexShader);
        DestroyShaderModule(GridFragmentShader);
        DestroyShaderModule(SkyboxVertexShader);
        DestroyShaderModule(SkyboxFragmentShader);
        DestroyShaderModule(ModelVertexShader);
        DestroyShaderModule(ModelFragmentShader);
        DestroyShaderModule(SkinnedModelVertexShader);
        DestroyShaderModule(SkinnedModelFragmentShader);
        DestroyShaderModule(ShadowVertexShader);
        DestroyShaderModule(SkinnedShadowVertexShader);
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
