using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Owns retained indexed meshes in device-local Vulkan buffers.</summary>
internal unsafe sealed class PersistentIndexedMeshStore
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Func<uint, MemoryPropertyFlags, uint> _findMemoryType;
    private readonly ILogger _logger;
    private readonly Dictionary<MeshHandle, Resource> _resources = [];
    private readonly List<Resource> _pendingUploads = [];
    private readonly Dictionary<Resource, PendingVertexRange> _pendingVertexUpdates = [];

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
    /// <param name="baseColorTexture">Sampled base-color texture.</param>
    /// <param name="normalTexture">Sampled tangent-space normal texture.</param>
    /// <param name="metallicRoughnessTexture">Sampled metallic-roughness texture.</param>
    /// <param name="metallic">Material metallic factor.</param>
    /// <param name="roughness">Material roughness factor.</param>
    /// <param name="baseColor">Material base-color multiplier.</param>
    /// <param name="doubleSided">Whether back-face culling is disabled.</param>
    public void Add(
        MeshHandle handle,
        ForwardModelVertex[] vertices,
        uint[] indices,
        TextureHandle baseColorTexture,
        TextureHandle normalTexture,
        TextureHandle metallicRoughnessTexture,
        float metallic,
        float roughness,
        System.Numerics.Vector4 baseColor,
        bool doubleSided)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (vertices.Length == 0 || indices.Length == 0)
        {
            _resources.Add(handle, new Resource(vertices, indices, baseColorTexture,
                normalTexture, metallicRoughnessTexture, metallic, roughness, baseColor,
                doubleSided));
            return;
        }
        var resource = new Resource(vertices, indices, baseColorTexture,
            normalTexture, metallicRoughnessTexture, metallic, roughness, baseColor, doubleSided)
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

    /// <summary>Updates retained CPU vertices and queues one coalesced GPU range upload.</summary>
    /// <param name="handle">Renderer-owned indexed mesh.</param>
    /// <param name="update">Replacement source and destination range.</param>
    public void UpdateVertices(MeshHandle handle, StaticMeshVertexUpdate update)
    {
        if (!_resources.TryGetValue(handle, out var resource))
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Mesh resource was not found.");
        if ((ulong)update.FirstVertex + (ulong)update.VertexCount >
            (ulong)resource.Vertices.Length)
            throw new ArgumentOutOfRangeException(nameof(update));
        var destinationIndex = checked((int)update.FirstVertex);
        for (var index = 0; index < update.VertexCount; index++)
        {
            var source = update.Vertices[update.SourceIndex + index];
            resource.Vertices[destinationIndex + index] = new ForwardModelVertex(
                source.Position, source.Normal, source.TexCoord, source.Tangent,
                source.Color * resource.BaseColor);
        }
        if (_pendingUploads.Contains(resource))
            return;
        var end = checked(destinationIndex + update.VertexCount);
        if (_pendingVertexUpdates.TryGetValue(resource, out var pending))
        {
            _pendingVertexUpdates[resource] = new PendingVertexRange(
                Math.Min(pending.FirstVertex, destinationIndex),
                Math.Max(pending.EndVertex, end));
        }
        else
            _pendingVertexUpdates.Add(resource, new PendingVertexRange(destinationIndex, end));
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
            checked((uint)resource.Indices.Length), resource.BaseColorTexture,
            resource.NormalTexture, resource.MetallicRoughnessTexture,
            resource.Metallic, resource.Roughness, resource.DoubleSided);
    }

    /// <summary>Removes a resource for fence-safe retirement.</summary>
    /// <param name="handle">Renderer-owned mesh handle.</param>
    /// <returns>The removed resource.</returns>
    public Resource Remove(MeshHandle handle)
    {
        if (!_resources.Remove(handle, out var resource))
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Mesh resource was not found.");
        _pendingUploads.Remove(resource);
        _pendingVertexUpdates.Remove(resource);
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
        if (_pendingUploads.Count == 0 && _pendingVertexUpdates.Count == 0)
            return;
        long totalBytes = 0;
        for (var index = 0; index < _pendingUploads.Count; index++)
        {
            var resource = _pendingUploads[index];
            totalBytes = checked(totalBytes +
                (long)resource.Vertices.Length * ForwardModelVertex.Stride +
                (long)resource.Indices.Length * sizeof(uint));
        }
        foreach (var pair in _pendingVertexUpdates)
        {
            totalBytes = checked(totalBytes +
                (long)(pair.Value.EndVertex - pair.Value.FirstVertex) *
                ForwardModelVertex.Stride);
        }
        var byteCount = checked((uint)totalBytes);
        _logger.LogDebug(
            "Uploading {MeshCount} indexed meshes and {UpdateCount} vertex ranges ({ByteCount} bytes)",
            _pendingUploads.Count, _pendingVertexUpdates.Count, byteCount);
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
        foreach (var pair in _pendingVertexUpdates)
        {
            var resource = pair.Key;
            var range = pair.Value;
            var count = range.EndVertex - range.FirstVertex;
            var vertexBytes = checked((nuint)(count * ForwardModelVertex.Stride));
            fixed (ForwardModelVertex* source = &resource.Vertices[range.FirstVertex])
            {
                System.Buffer.MemoryCopy(source, (byte*)staging.MappedPointer + offset,
                    vertexBytes, vertexBytes);
            }
            var vertexCopy = new BufferCopy
            {
                SrcOffset = staging.ByteOffset + offset,
                DstOffset = checked((ulong)range.FirstVertex * ForwardModelVertex.Stride),
                Size = (ulong)vertexBytes
            };
            _vk.CmdCopyBuffer(commandBuffer, staging.Buffer, resource.VertexBuffer, 1,
                &vertexCopy);
            offset += (ulong)vertexBytes;
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
        _pendingVertexUpdates.Clear();
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
        _pendingVertexUpdates.Clear();
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

    /// <summary>Owns one retained indexed mesh and its mutable CPU vertex shadow.</summary>
    internal sealed class Resource
    {
        internal ForwardModelVertex[] Vertices { get; }
        internal uint[] Indices { get; }
        internal TextureHandle BaseColorTexture { get; }
        internal TextureHandle NormalTexture { get; }
        internal TextureHandle MetallicRoughnessTexture { get; }
        internal float Metallic { get; }
        internal float Roughness { get; }
        internal System.Numerics.Vector4 BaseColor { get; }
        internal bool DoubleSided { get; }
        internal Silk.NET.Vulkan.Buffer VertexBuffer;
        internal DeviceMemory VertexMemory;
        internal Silk.NET.Vulkan.Buffer IndexBuffer;
        internal DeviceMemory IndexMemory;

        /// <summary>Creates one resource record.</summary>
        /// <param name="vertices">Owned upload vertices.</param>
        /// <param name="indices">Owned upload indices.</param>
        /// <param name="baseColorTexture">Sampled base-color texture.</param>
        /// <param name="normalTexture">Sampled normal texture.</param>
        /// <param name="metallicRoughnessTexture">Sampled metallic-roughness texture.</param>
        /// <param name="metallic">Material metallic factor.</param>
        /// <param name="roughness">Material roughness factor.</param>
        /// <param name="baseColor">Material base-color multiplier.</param>
        /// <param name="doubleSided">Whether back-face culling is disabled.</param>
        internal Resource(
            ForwardModelVertex[] vertices,
            uint[] indices,
            TextureHandle baseColorTexture,
            TextureHandle normalTexture,
            TextureHandle metallicRoughnessTexture,
            float metallic,
            float roughness,
            System.Numerics.Vector4 baseColor,
            bool doubleSided)
        {
            Vertices = vertices;
            Indices = indices;
            BaseColorTexture = baseColorTexture;
            NormalTexture = normalTexture;
            MetallicRoughnessTexture = metallicRoughnessTexture;
            Metallic = metallic;
            Roughness = roughness;
            BaseColor = baseColor;
            DoubleSided = doubleSided;
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
    /// <param name="BaseColorTexture">Sampled base-color texture.</param>
    /// <param name="NormalTexture">Sampled tangent-space normal texture.</param>
    /// <param name="MetallicRoughnessTexture">Sampled metallic-roughness texture.</param>
    /// <param name="Metallic">Material metallic factor.</param>
    /// <param name="Roughness">Material roughness factor.</param>
    /// <param name="DoubleSided">Whether back-face culling is disabled.</param>
    internal readonly record struct Binding(
        Silk.NET.Vulkan.Buffer VertexBuffer,
        Silk.NET.Vulkan.Buffer IndexBuffer,
        uint IndexCount,
        TextureHandle BaseColorTexture,
        TextureHandle NormalTexture,
        TextureHandle MetallicRoughnessTexture,
        float Metallic,
        float Roughness,
        bool DoubleSided);

    /// <summary>Describes one half-open pending vertex range.</summary>
    /// <param name="FirstVertex">First pending vertex.</param>
    /// <param name="EndVertex">Exclusive pending vertex end.</param>
    private readonly record struct PendingVertexRange(int FirstVertex, int EndVertex);
}
