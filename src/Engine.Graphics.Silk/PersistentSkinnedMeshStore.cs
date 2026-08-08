using System.Numerics;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Owns immutable skinned meshes and fence-safe per-frame joint palettes.</summary>
internal unsafe sealed class PersistentSkinnedMeshStore
{
    private const uint FrameCount = 2;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Func<uint, MemoryPropertyFlags, uint> _findMemoryType;
    private readonly DescriptorSetLayout _paletteLayout;
    private readonly DescriptorPool _palettePool;
    private readonly ILogger _logger;
    private readonly Dictionary<MeshHandle, Resource> _meshes = [];
    private readonly Dictionary<SkinPaletteHandle, Resource> _palettes = [];
    private readonly List<Resource> _pendingUploads = [];

    /// <summary>Creates an empty skinned mesh store.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="paletteLayout">Storage-buffer descriptor layout.</param>
    /// <param name="palettePool">Storage-buffer descriptor pool.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public PersistentSkinnedMeshStore(
        Vk vk,
        Device device,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        DescriptorSetLayout paletteLayout,
        DescriptorPool palettePool,
        ILogger logger)
    {
        _vk = vk;
        _device = device;
        _findMemoryType = findMemoryType;
        _paletteLayout = paletteLayout;
        _palettePool = palettePool;
        _logger = logger;
    }

    /// <summary>Creates geometry buffers and a reusable joint palette.</summary>
    /// <param name="meshHandle">Renderer-owned mesh handle.</param>
    /// <param name="paletteHandle">Renderer-owned palette handle.</param>
    /// <param name="vertices">Packed skinned vertices.</param>
    /// <param name="indices">Triangle-list indices.</param>
    /// <param name="texture">Sampled base-color texture.</param>
    /// <param name="bindPalette">Initial bind-pose skin matrices.</param>
    public void Add(
        MeshHandle meshHandle,
        SkinPaletteHandle paletteHandle,
        SkinnedForwardModelVertex[] vertices,
        uint[] indices,
        TextureHandle texture,
        ReadOnlySpan<Matrix4x4> bindPalette)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (bindPalette.Length == 0)
            throw new ArgumentException("A skinned mesh requires at least one joint.", nameof(bindPalette));
        var palette = bindPalette.ToArray();
        var resource = new Resource(vertices, indices, texture, paletteHandle, palette,
            new FrameVertexBuffers(_vk, _device, FrameCount,
                checked((uint)palette.Length), "skin palette", _findMemoryType, _logger,
                BufferUsageFlags.StorageBufferBit));
        try
        {
            if (vertices.Length > 0 && indices.Length > 0)
            {
                resource.VertexBuffer = CreateBuffer(
                    checked((ulong)vertices.Length * SkinnedForwardModelVertex.Stride),
                    BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                    out resource.VertexMemory);
                resource.IndexBuffer = CreateBuffer(
                    checked((ulong)indices.Length * sizeof(uint)),
                    BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
                    out resource.IndexMemory);
            }
            AllocatePaletteDescriptors(resource);
            _meshes.Add(meshHandle, resource);
            _palettes.Add(paletteHandle, resource);
            _pendingUploads.Add(resource);
        }
        catch
        {
            resource.Destroy(_vk, _device, _palettePool);
            throw;
        }
    }

    /// <summary>Gets whether a mesh belongs to this store.</summary>
    /// <param name="handle">Mesh handle.</param>
    /// <returns>True when present.</returns>
    public bool ContainsMesh(MeshHandle handle) => _meshes.ContainsKey(handle);

    /// <summary>Gets whether a palette belongs to this store.</summary>
    /// <param name="handle">Palette handle.</param>
    /// <returns>True when present.</returns>
    public bool ContainsPalette(SkinPaletteHandle handle) => _palettes.ContainsKey(handle);

    /// <summary>Copies new joint matrices into the retained palette snapshot.</summary>
    /// <param name="handle">Palette handle.</param>
    /// <param name="matrices">Matrices in skeleton order.</param>
    public void UpdatePalette(SkinPaletteHandle handle, ReadOnlySpan<Matrix4x4> matrices)
    {
        if (!_palettes.TryGetValue(handle, out var resource))
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Skin palette was not found.");
        if (matrices.Length != resource.Palette.Length)
            throw new ArgumentException("Skin palette joint count cannot change.", nameof(matrices));
        matrices.CopyTo(resource.Palette);
        resource.PaletteGeneration++;
    }

    /// <summary>Gets native bindings for one skinned draw.</summary>
    /// <param name="mesh">Mesh handle.</param>
    /// <param name="palette">Palette handle paired with this skeleton.</param>
    /// <param name="frameIndex">Active frame slot.</param>
    /// <returns>Geometry, texture, and palette bindings.</returns>
    public Binding GetBinding(
        MeshHandle mesh,
        SkinPaletteHandle palette,
        uint frameIndex)
    {
        if (!_meshes.TryGetValue(mesh, out var meshResource))
            throw new ArgumentOutOfRangeException(nameof(mesh), mesh, "Skinned mesh was not found.");
        if (!_palettes.TryGetValue(palette, out var paletteResource))
            throw new ArgumentOutOfRangeException(nameof(palette), palette, "Skin palette was not found.");
        if (meshResource.Palette.Length != paletteResource.Palette.Length)
            throw new InvalidOperationException("Skinned mesh and palette joint counts do not match.");
        return new Binding(meshResource.VertexBuffer, meshResource.IndexBuffer,
            checked((uint)meshResource.Indices.Length), meshResource.Texture,
            paletteResource.DescriptorSets[frameIndex]);
    }

    /// <summary>Records geometry uploads and updates the safe palette frame slot.</summary>
    /// <param name="commandBuffer">Command buffer receiving transfers.</param>
    /// <param name="transientArena">Active staging arena.</param>
    /// <param name="frameIndex">Active frame slot.</param>
    public void RecordPendingUploads(
        CommandBuffer commandBuffer,
        FrameTransientArena transientArena,
        uint frameIndex)
    {
        if (_pendingUploads.Count > 0)
        {
            long requiredBytes = 0;
            for (var index = 0; index < _pendingUploads.Count; index++)
            {
                var resource = _pendingUploads[index];
                requiredBytes = checked(requiredBytes +
                    (long)resource.Vertices.Length * SkinnedForwardModelVertex.Stride +
                    (long)resource.Indices.Length * sizeof(uint));
            }
            var byteCount = checked((uint)requiredBytes);
            if (byteCount > 0)
            {
                var staging = transientArena.Allocate(frameIndex, byteCount);
                var offset = 0UL;
                foreach (var resource in _pendingUploads)
                {
                    var vertexBytes = checked((nuint)(resource.Vertices.Length *
                        SkinnedForwardModelVertex.Stride));
                    fixed (SkinnedForwardModelVertex* source = resource.Vertices)
                    {
                        System.Buffer.MemoryCopy(source, (byte*)staging.MappedPointer + offset,
                            vertexBytes, vertexBytes);
                    }
                    var vertexCopy = new BufferCopy
                    {
                        SrcOffset = staging.ByteOffset + offset,
                        Size = (ulong)vertexBytes
                    };
                    if (vertexBytes > 0)
                        _vk.CmdCopyBuffer(commandBuffer, staging.Buffer,
                            resource.VertexBuffer, 1, &vertexCopy);
                    offset += (ulong)vertexBytes;
                    var indexBytes = checked((nuint)(resource.Indices.Length * sizeof(uint)));
                    fixed (uint* source = resource.Indices)
                    {
                        System.Buffer.MemoryCopy(source, (byte*)staging.MappedPointer + offset,
                            indexBytes, indexBytes);
                    }
                    var indexCopy = new BufferCopy
                    {
                        SrcOffset = staging.ByteOffset + offset,
                        Size = (ulong)indexBytes
                    };
                    if (indexBytes > 0)
                        _vk.CmdCopyBuffer(commandBuffer, staging.Buffer,
                            resource.IndexBuffer, 1, &indexCopy);
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
            }
            _pendingUploads.Clear();
        }
        foreach (var resource in _palettes.Values)
            UploadPalette(resource, frameIndex);
    }

    /// <summary>Removes and returns one mesh resource for deferred release.</summary>
    /// <param name="handle">Mesh handle.</param>
    /// <returns>Removed resource.</returns>
    public Resource RemoveMesh(MeshHandle handle)
    {
        if (!_meshes.Remove(handle, out var resource))
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Skinned mesh was not found.");
        _palettes.Remove(resource.PaletteHandle);
        _pendingUploads.Remove(resource);
        return resource;
    }

    /// <summary>Destroys a retired resource.</summary>
    /// <param name="resource">Resource no longer used by GPU work.</param>
    public void Release(Resource resource) => resource.Destroy(_vk, _device, _palettePool);

    /// <summary>Destroys all owned resources.</summary>
    public void Destroy()
    {
        foreach (var resource in _meshes.Values)
            resource.Destroy(_vk, _device, _palettePool);
        _meshes.Clear();
        _palettes.Clear();
        _pendingUploads.Clear();
    }

    /// <summary>Allocates two palette descriptors and their mapped buffers.</summary>
    /// <param name="resource">Resource receiving descriptor sets.</param>
    private void AllocatePaletteDescriptors(Resource resource)
    {
        var layouts = stackalloc DescriptorSetLayout[(int)FrameCount];
        for (var index = 0; index < FrameCount; index++)
            layouts[index] = _paletteLayout;
        var allocation = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _palettePool,
            DescriptorSetCount = FrameCount,
            PSetLayouts = layouts
        };
        fixed (DescriptorSet* sets = resource.DescriptorSets)
            Check(_vk.AllocateDescriptorSets(_device, &allocation, sets),
                "allocate skin palette descriptors");
        for (uint frame = 0; frame < FrameCount; frame++)
        {
            resource.PaletteBuffers.Ensure(frame, checked((uint)resource.Palette.Length),
                checked((uint)sizeof(Matrix4x4)));
            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = resource.PaletteBuffers.GetBuffer(frame),
                Offset = 0,
                Range = checked((ulong)resource.Palette.Length * (ulong)sizeof(Matrix4x4))
            };
            var descriptor = resource.DescriptorSets[frame];
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptor,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufferInfo
            };
            _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        }
    }

    /// <summary>Copies the latest palette into the active safe frame slot.</summary>
    /// <param name="resource">Palette resource.</param>
    /// <param name="frameIndex">Active frame slot.</param>
    private static void UploadPalette(Resource resource, uint frameIndex)
    {
        if (resource.UploadedGenerations[frameIndex] == resource.PaletteGeneration)
            return;
        var byteCount = checked((nuint)(resource.Palette.Length * sizeof(Matrix4x4)));
        fixed (Matrix4x4* source = resource.Palette)
        {
            System.Buffer.MemoryCopy(source, resource.PaletteBuffers.GetMappedPointer(frameIndex),
                byteCount, byteCount);
        }
        resource.UploadedGenerations[frameIndex] = resource.PaletteGeneration;
    }

    /// <summary>Creates one device-local geometry buffer.</summary>
    /// <param name="size">Buffer size.</param>
    /// <param name="usage">Buffer usage.</param>
    /// <param name="memory">Allocated memory.</param>
    /// <returns>Bound buffer.</returns>
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
        Check(_vk.CreateBuffer(_device, &info, null, out var buffer),
            "create skinned mesh buffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit)
        };
        Check(_vk.AllocateMemory(_device, &allocation, null, out memory),
            "allocate skinned mesh memory");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0),
            "bind skinned mesh memory");
        return buffer;
    }

    /// <summary>Throws for a Vulkan failure.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }

    /// <summary>Owns one skinned mesh and palette allocation.</summary>
    internal sealed class Resource
    {
        internal SkinnedForwardModelVertex[] Vertices { get; }
        internal uint[] Indices { get; }
        internal TextureHandle Texture { get; }
        internal SkinPaletteHandle PaletteHandle { get; }
        internal Matrix4x4[] Palette { get; }
        internal FrameVertexBuffers PaletteBuffers { get; }
        internal DescriptorSet[] DescriptorSets { get; } = new DescriptorSet[FrameCount];
        internal ulong[] UploadedGenerations { get; } = new ulong[FrameCount];
        internal ulong PaletteGeneration = 1;
        internal Silk.NET.Vulkan.Buffer VertexBuffer;
        internal DeviceMemory VertexMemory;
        internal Silk.NET.Vulkan.Buffer IndexBuffer;
        internal DeviceMemory IndexMemory;

        /// <summary>Creates a resource record.</summary>
        /// <param name="vertices">Owned vertices.</param>
        /// <param name="indices">Owned indices.</param>
        /// <param name="texture">Texture handle.</param>
        /// <param name="paletteHandle">Renderer-owned palette identity.</param>
        /// <param name="palette">Owned current palette.</param>
        /// <param name="paletteBuffers">Per-frame palette buffers.</param>
        internal Resource(
            SkinnedForwardModelVertex[] vertices,
            uint[] indices,
            TextureHandle texture,
            SkinPaletteHandle paletteHandle,
            Matrix4x4[] palette,
            FrameVertexBuffers paletteBuffers)
        {
            Vertices = vertices;
            Indices = indices;
            Texture = texture;
            PaletteHandle = paletteHandle;
            Palette = palette;
            PaletteBuffers = paletteBuffers;
        }

        /// <summary>Destroys native allocations and descriptor sets.</summary>
        /// <param name="vk">Vulkan API.</param>
        /// <param name="device">Logical device.</param>
        /// <param name="descriptorPool">Palette descriptor pool.</param>
        internal void Destroy(Vk vk, Device device, DescriptorPool descriptorPool)
        {
            PaletteBuffers.Destroy();
            fixed (DescriptorSet* sets = DescriptorSets)
            {
                if (DescriptorSets[0].Handle != 0)
                    vk.FreeDescriptorSets(device, descriptorPool, FrameCount, sets);
            }
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

    /// <summary>Describes native bindings for one skinned draw.</summary>
    /// <param name="VertexBuffer">Vertex buffer.</param>
    /// <param name="IndexBuffer">Index buffer.</param>
    /// <param name="IndexCount">Index count.</param>
    /// <param name="Texture">Texture handle.</param>
    /// <param name="PaletteDescriptor">Palette descriptor set.</param>
    internal readonly record struct Binding(
        Silk.NET.Vulkan.Buffer VertexBuffer,
        Silk.NET.Vulkan.Buffer IndexBuffer,
        uint IndexCount,
        TextureHandle Texture,
        DescriptorSet PaletteDescriptor);
}
