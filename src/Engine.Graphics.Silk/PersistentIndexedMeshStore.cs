using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Owns immutable indexed meshes in device-local Vulkan buffers.</summary>
internal unsafe sealed class PersistentIndexedMeshStore
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Func<uint, MemoryPropertyFlags, uint> _findMemoryType;
    private readonly ILogger _logger;
    private readonly Dictionary<MeshHandle, Resource> _resources = [];
    private readonly List<Resource> _pendingUploads = [];

    /// <summary>Creates an empty indexed mesh store.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public PersistentIndexedMeshStore(
        Vk vk,
        Device device,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        ILogger logger)
    {
        _vk = vk;
        _device = device;
        _findMemoryType = findMemoryType;
        _logger = logger;
    }

    /// <summary>Creates device-local buffers and queues their initial upload.</summary>
    /// <param name="handle">Renderer-owned mesh handle.</param>
    /// <param name="vertices">Compact shaded vertices.</param>
    /// <param name="indices">Triangle-list indices.</param>
    /// <param name="texture">Sampled base-color texture.</param>
    public void Add(
        MeshHandle handle,
        ForwardModelVertex[] vertices,
        uint[] indices,
        TextureHandle texture)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (vertices.Length == 0 || indices.Length == 0)
        {
            _resources.Add(handle, new Resource(vertices, indices, texture));
            return;
        }
        var resource = new Resource(vertices, indices, texture)
        {
            VertexBuffer = CreateBuffer(checked((ulong)vertices.Length * ForwardModelVertex.Stride),
                BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                out var vertexMemory),
            VertexMemory = vertexMemory,
            IndexBuffer = CreateBuffer(checked((ulong)indices.Length * sizeof(uint)),
                BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
                out var indexMemory),
            IndexMemory = indexMemory
        };
        _resources.Add(handle, resource);
        _pendingUploads.Add(resource);
    }

    /// <summary>Gets whether a handle belongs to the indexed model store.</summary>
    /// <param name="handle">Renderer-owned mesh handle.</param>
    /// <returns>True when the resource exists.</returns>
    public bool Contains(MeshHandle handle) => _resources.ContainsKey(handle);

    /// <summary>Gets indexed draw bindings.</summary>
    /// <param name="handle">Renderer-owned mesh handle.</param>
    /// <returns>Vertex buffer, index buffer, and index count.</returns>
    public Binding GetBinding(MeshHandle handle)
    {
        if (!_resources.TryGetValue(handle, out var resource))
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Mesh resource was not found.");
        return new Binding(resource.VertexBuffer, resource.IndexBuffer,
            checked((uint)resource.Indices.Length), resource.Texture);
    }

    /// <summary>Removes a resource for fence-safe retirement.</summary>
    /// <param name="handle">Renderer-owned mesh handle.</param>
    /// <returns>The removed resource.</returns>
    public Resource Remove(MeshHandle handle)
    {
        if (!_resources.Remove(handle, out var resource))
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Mesh resource was not found.");
        _pendingUploads.Remove(resource);
        return resource;
    }

    /// <summary>Records pending uploads through the active transient staging arena.</summary>
    /// <param name="commandBuffer">Command buffer receiving transfer commands.</param>
    /// <param name="transientArena">Active staging arena.</param>
    /// <param name="frameIndex">Active frame slot.</param>
    public void RecordPendingUploads(
        CommandBuffer commandBuffer,
        FrameTransientArena transientArena,
        uint frameIndex)
    {
        if (_pendingUploads.Count == 0)
            return;
        long totalBytes = 0;
        for (var index = 0; index < _pendingUploads.Count; index++)
        {
            var resource = _pendingUploads[index];
            totalBytes = checked(totalBytes +
                (long)resource.Vertices.Length * ForwardModelVertex.Stride +
                (long)resource.Indices.Length * sizeof(uint));
        }
        var byteCount = checked((uint)totalBytes);
        _logger.LogDebug("Uploading {MeshCount} indexed meshes ({ByteCount} bytes)",
            _pendingUploads.Count, byteCount);
        var staging = transientArena.Allocate(frameIndex, byteCount);
        var offset = 0UL;
        foreach (var resource in _pendingUploads)
        {
            var vertexBytes = checked((nuint)(resource.Vertices.Length * ForwardModelVertex.Stride));
            fixed (ForwardModelVertex* source = resource.Vertices)
                System.Buffer.MemoryCopy(source, (byte*)staging.MappedPointer + offset,
                    vertexBytes, vertexBytes);
            var vertexCopy = new BufferCopy
            {
                SrcOffset = staging.ByteOffset + offset,
                Size = (ulong)vertexBytes
            };
            _vk.CmdCopyBuffer(commandBuffer, staging.Buffer, resource.VertexBuffer, 1,
                &vertexCopy);
            offset += (ulong)vertexBytes;

            var indexBytes = checked((nuint)(resource.Indices.Length * sizeof(uint)));
            fixed (uint* source = resource.Indices)
                System.Buffer.MemoryCopy(source, (byte*)staging.MappedPointer + offset,
                    indexBytes, indexBytes);
            var indexCopy = new BufferCopy
            {
                SrcOffset = staging.ByteOffset + offset,
                Size = (ulong)indexBytes
            };
            _vk.CmdCopyBuffer(commandBuffer, staging.Buffer, resource.IndexBuffer, 1,
                &indexCopy);
            offset += (ulong)indexBytes;
        }
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit
        };
        _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
            PipelineStageFlags.VertexInputBit, 0, 1, &barrier, 0, null, 0, null);
        _pendingUploads.Clear();
    }

    /// <summary>Destroys one retired resource.</summary>
    /// <param name="resource">Resource no longer referenced by in-flight work.</param>
    public void Release(Resource resource)
    {
        resource.Destroy(_vk, _device);
    }

    /// <summary>Destroys every owned resource.</summary>
    public void Destroy()
    {
        foreach (var resource in _resources.Values)
            resource.Destroy(_vk, _device);
        _resources.Clear();
        _pendingUploads.Clear();
    }

    /// <summary>Creates one device-local buffer.</summary>
    /// <param name="size">Required byte size.</param>
    /// <param name="usage">Vulkan usage flags.</param>
    /// <param name="memory">Allocated device memory.</param>
    /// <returns>The bound buffer.</returns>
    private Silk.NET.Vulkan.Buffer CreateBuffer(
        ulong size,
        BufferUsageFlags usage,
        out DeviceMemory memory)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateBuffer(_device, &info, null, out var buffer), "create indexed mesh buffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit)
        };
        Check(_vk.AllocateMemory(_device, &allocation, null, out memory),
            "allocate indexed mesh memory");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "bind indexed mesh memory");
        return buffer;
    }

    /// <summary>Throws when Vulkan reports a failure.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }

    /// <summary>Owns one immutable indexed mesh and its pending source data.</summary>
    internal sealed class Resource
    {
        internal ForwardModelVertex[] Vertices { get; }
        internal uint[] Indices { get; }
        internal TextureHandle Texture { get; }
        internal Silk.NET.Vulkan.Buffer VertexBuffer;
        internal DeviceMemory VertexMemory;
        internal Silk.NET.Vulkan.Buffer IndexBuffer;
        internal DeviceMemory IndexMemory;

        /// <summary>Creates one resource record.</summary>
        /// <param name="vertices">Owned upload vertices.</param>
        /// <param name="indices">Owned upload indices.</param>
        internal Resource(
            ForwardModelVertex[] vertices,
            uint[] indices,
            TextureHandle texture)
        {
            Vertices = vertices;
            Indices = indices;
            Texture = texture;
        }

        /// <summary>Destroys Vulkan buffer resources.</summary>
        /// <param name="vk">Vulkan API.</param>
        /// <param name="device">Owning logical device.</param>
        internal void Destroy(Vk vk, Device device)
        {
            if (VertexBuffer.Handle != 0)
                vk.DestroyBuffer(device, VertexBuffer, null);
            if (VertexMemory.Handle != 0)
                vk.FreeMemory(device, VertexMemory, null);
            if (IndexBuffer.Handle != 0)
                vk.DestroyBuffer(device, IndexBuffer, null);
            if (IndexMemory.Handle != 0)
                vk.FreeMemory(device, IndexMemory, null);
        }
    }

    /// <summary>Describes bindings required for one indexed draw.</summary>
    /// <param name="VertexBuffer">Vertex buffer.</param>
    /// <param name="IndexBuffer">Index buffer.</param>
    /// <param name="IndexCount">Number of indices.</param>
    /// <param name="Texture">Sampled base-color texture.</param>
    internal readonly record struct Binding(
        Silk.NET.Vulkan.Buffer VertexBuffer,
        Silk.NET.Vulkan.Buffer IndexBuffer,
        uint IndexCount,
        TextureHandle Texture);
}
