using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>
/// Owns one persistently mapped, growable vertex buffer per frame in flight.
/// </summary>
internal unsafe sealed class FrameVertexBuffers
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Func<uint, MemoryPropertyFlags, uint> _findMemoryType;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly uint _minimumCapacity;
    private readonly Silk.NET.Vulkan.Buffer[] _buffers;
    private readonly DeviceMemory[] _memories;
    private readonly nint[] _mappedPointers;
    private readonly uint[] _capacities;

    /// <summary>
    /// Creates a per-frame vertex-buffer owner.
    /// </summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="frameCount">Number of frames in flight.</param>
    /// <param name="minimumCapacity">Minimum allocation size in vertices.</param>
    /// <param name="name">Diagnostic buffer name.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="logger">Backend logger.</param>
    public FrameVertexBuffers(
        Vk vk,
        Device device,
        uint frameCount,
        uint minimumCapacity,
        string name,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        ILogger logger)
    {
        _vk = vk;
        _device = device;
        _minimumCapacity = minimumCapacity;
        _name = name;
        _findMemoryType = findMemoryType;
        _logger = logger;
        _buffers = new Silk.NET.Vulkan.Buffer[frameCount];
        _memories = new DeviceMemory[frameCount];
        _mappedPointers = new nint[frameCount];
        _capacities = new uint[frameCount];
    }

    /// <summary>Ensures one frame buffer can hold the requested vertex count.</summary>
    /// <param name="frameIndex">Frame slot.</param>
    /// <param name="requiredVertices">Required vertex count.</param>
    /// <param name="vertexStride">Vertex size in bytes.</param>
    public void Ensure(uint frameIndex, uint requiredVertices, uint vertexStride)
    {
        if (_capacities[frameIndex] >= requiredVertices)
            return;

        DestroyFrame(frameIndex);
        _capacities[frameIndex] = Math.Max(requiredVertices, _minimumCapacity);
        var bufferSize = (nuint)(_capacities[frameIndex] * vertexStride);
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.VertexBufferBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateBuffer(_device, &bufferInfo, null, out _buffers[frameIndex]), "create buffer");
        _vk.GetBufferMemoryRequirements(_device, _buffers[frameIndex], out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        Check(_vk.AllocateMemory(_device, &allocation, null, out _memories[frameIndex]), "allocate memory");
        Check(_vk.BindBufferMemory(_device, _buffers[frameIndex], _memories[frameIndex], 0), "bind memory");
        void* mapped;
        Check(_vk.MapMemory(_device, _memories[frameIndex], 0, bufferSize, 0, &mapped), "map memory");
        _mappedPointers[frameIndex] = (nint)mapped;
        _logger.LogDebug("Frame {Frame} {Name} buffer capacity is {Capacity} vertices",
            frameIndex, _name, _capacities[frameIndex]);
    }

    /// <summary>Gets the Vulkan buffer for a frame slot.</summary>
    /// <param name="frameIndex">Frame slot.</param>
    /// <returns>The Vulkan buffer.</returns>
    public Silk.NET.Vulkan.Buffer GetBuffer(uint frameIndex) => _buffers[frameIndex];

    /// <summary>Gets the mapped pointer for a frame slot.</summary>
    /// <param name="frameIndex">Frame slot.</param>
    /// <returns>The mapped pointer.</returns>
    public void* GetMappedPointer(uint frameIndex) => (void*)_mappedPointers[frameIndex];

    /// <summary>Destroys every owned buffer and allocation.</summary>
    public void Destroy()
    {
        for (uint frame = 0; frame < _buffers.Length; frame++)
            DestroyFrame(frame);
    }

    /// <summary>Destroys one frame slot if allocated.</summary>
    /// <param name="frameIndex">Frame slot.</param>
    private void DestroyFrame(uint frameIndex)
    {
        if (_buffers[frameIndex].Handle == 0)
            return;

        _vk.DestroyBuffer(_device, _buffers[frameIndex], null);
        _vk.FreeMemory(_device, _memories[frameIndex], null);
        _buffers[frameIndex] = default;
        _memories[frameIndex] = default;
        _mappedPointers[frameIndex] = 0;
        _capacities[frameIndex] = 0;
    }

    /// <summary>Throws when a Vulkan buffer operation fails.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {_name} {operation}: {result}");
    }
}
