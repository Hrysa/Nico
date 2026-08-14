using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>GPU layout for one directional, point, or spot light.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSceneLight
{
    public Vector4 PositionRange;
    public Vector4 DirectionType;
    public Vector4 ColorIntensity;
    public Vector4 SpotParameters;

    /// <summary>Packs one renderer-independent light for shader consumption.</summary>
    /// <param name="light">Collected scene light.</param>
    /// <param name="localShadowsEnabled">Whether the active SRP populated local shadows.</param>
    /// <returns>Four-vector GPU representation.</returns>
    public static GpuSceneLight Create(SceneLight light, bool localShadowsEnabled) => new()
    {
        PositionRange = new Vector4(light.Position, light.Range),
        DirectionType = new Vector4(light.Direction, (float)light.Type),
        ColorIntensity = new Vector4(light.Color, light.Intensity),
        SpotParameters = new Vector4(
            light.InnerConeCosine,
            light.OuterConeCosine,
            localShadowsEnabled ? light.ShadowIndex : -1f,
            0f)
    };
}

/// <summary>Owns persistently mapped per-frame uniform buffers for one view's lights.</summary>
internal unsafe sealed class SceneLightGpuBuffer
{
    private const int FrameCount = 2;
    private const int HeaderSize = sizeof(float) * 12;
    private const int BufferSize = HeaderSize +
        SceneLightSet.MaximumLights * sizeof(float) * 16;
    private readonly Silk.NET.Vulkan.Buffer[] _buffers =
        new Silk.NET.Vulkan.Buffer[FrameCount];
    private readonly DeviceMemory[] _memories = new DeviceMemory[FrameCount];
    private readonly nint[] _mappedData = new nint[FrameCount];
    private readonly DescriptorSet[] _descriptorSets = new DescriptorSet[FrameCount];

    /// <summary>Creates per-frame buffers and descriptor sets.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning device.</param>
    /// <param name="descriptorSetLayout">Lighting descriptor layout.</param>
    /// <param name="descriptorPool">Lighting descriptor pool.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    public void Create(
        Vk vk,
        Device device,
        DescriptorSetLayout descriptorSetLayout,
        DescriptorPool descriptorPool,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType)
    {
        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            CreateFrame(vk, device, descriptorSetLayout, descriptorPool,
                findMemoryType, frameIndex);
        }
    }

    /// <summary>Uploads camera, ambient, and visible-light data for one frame.</summary>
    /// <param name="frameIndex">Frame-in-flight index.</param>
    /// <param name="lights">Collected scene lights.</param>
    /// <param name="camera">Active render camera.</param>
    /// <param name="localShadowsEnabled">Whether local shadow maps were populated.</param>
    public void Update(
        uint frameIndex,
        SceneLightSet lights,
        RenderCameraData camera,
        bool localShadowsEnabled)
    {
        if (frameIndex >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        ArgumentNullException.ThrowIfNull(lights);
        var cameraPosition = camera.IsValid && Matrix4x4.Invert(camera.View, out var inverseView)
            ? inverseView.Translation : Vector3.Zero;
        var vectors = (Vector4*)_mappedData[frameIndex];
        vectors[0] = new Vector4(lights.AmbientColor, lights.AmbientIntensity);
        vectors[1] = new Vector4(cameraPosition, lights.Count);
        vectors[2] = new Vector4(lights.MainDirectionalIndex, 0f, 0f, 0f);
        var destination = (GpuSceneLight*)(vectors + 3);
        var source = lights.Lights;
        for (var index = 0; index < source.Length; index++)
            destination[index] = GpuSceneLight.Create(source[index], localShadowsEnabled);
    }

    /// <summary>Gets the lighting descriptor for one frame in flight.</summary>
    /// <param name="frameIndex">Frame-in-flight index.</param>
    /// <returns>Uniform-buffer descriptor set.</returns>
    public DescriptorSet GetDescriptorSet(uint frameIndex)
    {
        if (frameIndex >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return _descriptorSets[frameIndex];
    }

    /// <summary>Releases buffers, mappings, memory, and descriptors.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning device.</param>
    /// <param name="descriptorPool">Pool that allocated the lighting sets.</param>
    public void Destroy(Vk vk, Device device, DescriptorPool descriptorPool)
    {
        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            if (_descriptorSets[frameIndex].Handle != 0)
            {
                var descriptor = _descriptorSets[frameIndex];
                vk.FreeDescriptorSets(device, descriptorPool, 1, &descriptor);
                _descriptorSets[frameIndex] = default;
            }
            if (_mappedData[frameIndex] != 0)
            {
                vk.UnmapMemory(device, _memories[frameIndex]);
                _mappedData[frameIndex] = 0;
            }
            if (_buffers[frameIndex].Handle != 0)
                vk.DestroyBuffer(device, _buffers[frameIndex], null);
            if (_memories[frameIndex].Handle != 0)
                vk.FreeMemory(device, _memories[frameIndex], null);
            _buffers[frameIndex] = default;
            _memories[frameIndex] = default;
        }
    }

    /// <summary>Creates one persistently mapped uniform buffer and descriptor.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning device.</param>
    /// <param name="descriptorSetLayout">Lighting descriptor layout.</param>
    /// <param name="descriptorPool">Lighting descriptor pool.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="frameIndex">Frame slot to initialize.</param>
    private void CreateFrame(
        Vk vk,
        Device device,
        DescriptorSetLayout descriptorSetLayout,
        DescriptorPool descriptorPool,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        int frameIndex)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = BufferSize,
            Usage = BufferUsageFlags.UniformBufferBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(vk.CreateBuffer(device, &bufferInfo, null, out _buffers[frameIndex]),
            "create scene-light buffer");
        vk.GetBufferMemoryRequirements(device, _buffers[frameIndex], out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        Check(vk.AllocateMemory(device, &allocation, null, out _memories[frameIndex]),
            "allocate scene-light memory");
        Check(vk.BindBufferMemory(device, _buffers[frameIndex], _memories[frameIndex], 0),
            "bind scene-light memory");
        void* mapped;
        Check(vk.MapMemory(device, _memories[frameIndex], 0, BufferSize, 0, &mapped),
            "map scene-light memory");
        _mappedData[frameIndex] = (nint)mapped;
        new Span<byte>(mapped, BufferSize).Clear();

        var layout = descriptorSetLayout;
        var descriptorAllocation = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        Check(vk.AllocateDescriptorSets(device, &descriptorAllocation,
            out _descriptorSets[frameIndex]), "allocate scene-light descriptor");
        var descriptorBuffer = new DescriptorBufferInfo
        {
            Buffer = _buffers[frameIndex],
            Range = BufferSize
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSets[frameIndex],
            DstBinding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            PBufferInfo = &descriptorBuffer
        };
        vk.UpdateDescriptorSets(device, 1, &write, 0, null);
    }

    /// <summary>Throws when Vulkan reports a failed lighting-buffer operation.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }
}
