using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Stores retained colored meshes in independently growable Vulkan buffer pages.</summary>
internal unsafe sealed class PersistentVertexArena
{
    private const uint DefaultPageVertices = 65_536;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Func<uint, MemoryPropertyFlags, uint> _findMemoryType;
    private readonly ILogger _logger;
    private readonly List<Page> _pages = [];
    private readonly Dictionary<MeshHandle, Allocation> _allocations = [];
    private readonly List<PendingUpload> _pendingUploads = [];

    /// <summary>Creates an empty persistent vertex arena.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public PersistentVertexArena(
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

    /// <summary>Allocates and uploads one retained mesh.</summary>
    /// <param name="handle">Resource handle.</param>
    /// <param name="vertices">Initial vertices.</param>
    public void Add(MeshHandle handle, Vertex[] vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (_allocations.ContainsKey(handle))
            throw new ArgumentException("Mesh handle is already allocated.", nameof(handle));
        var count = checked((uint)vertices.Length);
        if (count == 0)
        {
            _allocations.Add(handle, default);
            return;
        }

        for (var pageIndex = 0; pageIndex < _pages.Count; pageIndex++)
        {
            if (_pages[pageIndex].TryAllocate(count, out var firstVertex))
            {
                var allocation = new Allocation(pageIndex, firstVertex, count);
                _allocations.Add(handle, allocation);
                QueueUpload(allocation, 0, vertices);
                return;
            }
        }

        var capacity = Math.Max(DefaultPageVertices, RoundUpPowerOfTwo(count));
        var page = CreatePage(capacity);
        _pages.Add(page);
        if (!page.TryAllocate(count, out var offset))
            throw new InvalidOperationException("New persistent vertex page could not satisfy its first allocation.");
        var created = new Allocation(_pages.Count - 1, offset, count);
        _allocations.Add(handle, created);
        QueueUpload(created, 0, vertices);
    }

    /// <summary>Uploads a changed range without moving the mesh allocation.</summary>
    /// <param name="handle">Mesh to update.</param>
    /// <param name="update">Replacement range.</param>
    public void Update(MeshHandle handle, MeshUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Vertices);
        var allocation = GetAllocation(handle);
        if ((ulong)update.FirstVertex + (ulong)update.Vertices.Length > allocation.VertexCount)
            throw new ArgumentOutOfRangeException(nameof(update), "Mesh update exceeds its allocation.");
        QueueUpload(allocation, update.FirstVertex, update.Vertices);
    }

    /// <summary>Removes a mesh lookup and returns its range for fence-safe retirement.</summary>
    /// <param name="handle">Mesh to remove.</param>
    /// <returns>Allocation that must be released after in-flight draws complete.</returns>
    public Allocation Remove(MeshHandle handle)
    {
        if (!_allocations.Remove(handle, out var allocation))
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Mesh resource was not found.");
        return allocation;
    }

    /// <summary>Returns a retired range to its page.</summary>
    /// <param name="allocation">Allocation no longer referenced by the GPU.</param>
    public void Release(Allocation allocation)
    {
        if (allocation.VertexCount > 0)
            _pages[allocation.PageIndex].Release(allocation.FirstVertex, allocation.VertexCount);
    }

    /// <summary>Gets the Vulkan binding for a retained mesh.</summary>
    /// <param name="handle">Mesh resource.</param>
    /// <returns>Buffer, byte offset, and vertex count.</returns>
    public MeshBinding GetBinding(MeshHandle handle)
    {
        var allocation = GetAllocation(handle);
        if (allocation.VertexCount == 0)
            return default;
        return new MeshBinding(
            _pages[allocation.PageIndex].Buffer,
            (ulong)allocation.FirstVertex * Vertex.Stride,
            allocation.VertexCount);
    }

    /// <summary>Records all pending range uploads before retained meshes are drawn.</summary>
    /// <param name="commandBuffer">Command buffer receiving transfer commands.</param>
    /// <param name="transientArena">Active frame's mapped transient arena.</param>
    /// <param name="frameIndex">Active frame slot.</param>
    public void RecordPendingUploads(
        CommandBuffer commandBuffer,
        FrameTransientArena transientArena,
        uint frameIndex)
    {
        if (_pendingUploads.Count == 0)
            return;
        var totalBytes = checked((uint)_pendingUploads.Sum(upload => (long)upload.Vertices.Length * Vertex.Stride));
        var staging = transientArena.Allocate(frameIndex, totalBytes);
        var stagingOffset = 0UL;
        foreach (var upload in _pendingUploads)
        {
            var byteCount = checked((nuint)(upload.Vertices.Length * Vertex.Stride));
            fixed (Vertex* source = upload.Vertices)
            {
                var destination = (byte*)staging.MappedPointer + stagingOffset;
                System.Buffer.MemoryCopy(source, destination, byteCount, byteCount);
            }
            var region = new BufferCopy
            {
                SrcOffset = staging.ByteOffset + stagingOffset,
                DstOffset = (ulong)(upload.Allocation.FirstVertex + upload.RelativeFirstVertex) * Vertex.Stride,
                Size = (ulong)byteCount
            };
            var destinationBuffer = _pages[upload.Allocation.PageIndex].Buffer;
            _vk.CmdCopyBuffer(commandBuffer, staging.Buffer, destinationBuffer, 1, &region);
            stagingOffset += (ulong)byteCount;
        }
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.VertexAttributeReadBit
        };
        _vk.CmdPipelineBarrier(commandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.VertexInputBit,
            0, 1, &barrier, 0, null, 0, null);
        _pendingUploads.Clear();
    }

    /// <summary>Destroys all pages and allocations.</summary>
    public void Destroy()
    {
        foreach (var page in _pages)
            page.Destroy(_vk, _device);
        _pages.Clear();
        _allocations.Clear();
        _pendingUploads.Clear();
    }

    /// <summary>Gets one allocation or throws for a stale handle.</summary>
    /// <param name="handle">Mesh resource.</param>
    /// <returns>Current allocation.</returns>
    private Allocation GetAllocation(MeshHandle handle)
    {
        return _allocations.TryGetValue(handle, out var allocation)
            ? allocation
            : throw new ArgumentOutOfRangeException(nameof(handle), handle, "Mesh resource was not found.");
    }

    /// <summary>Queues vertices for an incremental staging upload.</summary>
    /// <param name="allocation">Destination allocation.</param>
    /// <param name="relativeFirstVertex">Offset within the allocation.</param>
    /// <param name="vertices">Vertices to copy.</param>
    private void QueueUpload(Allocation allocation, uint relativeFirstVertex, Vertex[] vertices)
    {
        if (vertices.Length == 0)
            return;
        _pendingUploads.Add(new PendingUpload(allocation, relativeFirstVertex, vertices.ToArray()));
    }

    /// <summary>Creates one mapped Vulkan vertex-buffer page.</summary>
    /// <param name="capacity">Page capacity in vertices.</param>
    /// <returns>Created page.</returns>
    private Page CreatePage(uint capacity)
    {
        var size = (nuint)(capacity * Vertex.Stride);
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "create page buffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var memoryInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit)
        };
        Check(_vk.AllocateMemory(_device, &memoryInfo, null, out var memory), "allocate page memory");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "bind page memory");
        _logger.LogDebug("Persistent vertex page capacity is {Capacity} vertices", capacity);
        return new Page(buffer, memory, capacity);
    }

    /// <summary>Rounds a positive integer up to a power of two.</summary>
    /// <param name="value">Value to round.</param>
    /// <returns>Smallest power of two not less than the value.</returns>
    private static uint RoundUpPowerOfTwo(uint value)
    {
        if (value <= 1)
            return 1;
        var rounded = System.Numerics.BitOperations.RoundUpToPowerOf2(value);
        return rounded == 0 ? value : rounded;
    }

    /// <summary>Throws when Vulkan reports an error.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }

    /// <summary>Identifies one suballocated range.</summary>
    /// <param name="PageIndex">Owning page index.</param>
    /// <param name="FirstVertex">First vertex in the page.</param>
    /// <param name="VertexCount">Allocated vertex count.</param>
    internal readonly record struct Allocation(int PageIndex, uint FirstVertex, uint VertexCount);

    /// <summary>Describes one Vulkan draw binding.</summary>
    /// <param name="Buffer">Vertex buffer.</param>
    /// <param name="ByteOffset">Byte offset within the buffer.</param>
    /// <param name="VertexCount">Number of vertices.</param>
    internal readonly record struct MeshBinding(
        Silk.NET.Vulkan.Buffer Buffer,
        ulong ByteOffset,
        uint VertexCount);

    /// <summary>Stores one queued range upload.</summary>
    /// <param name="Allocation">Destination allocation.</param>
    /// <param name="RelativeFirstVertex">Destination offset within the allocation.</param>
    /// <param name="Vertices">Owned upload data.</param>
    private sealed record PendingUpload(
        Allocation Allocation,
        uint RelativeFirstVertex,
        Vertex[] Vertices);

    /// <summary>Owns one mapped Vulkan buffer and its free ranges.</summary>
    private sealed class Page
    {
        private readonly List<FreeRange> _freeRanges;

        /// <summary>Gets the Vulkan buffer.</summary>
        public Silk.NET.Vulkan.Buffer Buffer { get; }

        /// <summary>Gets the Vulkan device memory.</summary>
        public DeviceMemory Memory { get; }

        /// <summary>Creates one page.</summary>
        /// <param name="buffer">Vulkan buffer.</param>
        /// <param name="memory">Bound memory.</param>
        /// <param name="capacity">Capacity in vertices.</param>
        public Page(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory, uint capacity)
        {
            Buffer = buffer;
            Memory = memory;
            _freeRanges = [new FreeRange(0, capacity)];
        }

        /// <summary>Attempts to reserve a contiguous range.</summary>
        /// <param name="count">Required vertices.</param>
        /// <param name="firstVertex">Allocated first vertex.</param>
        /// <returns>True when a range was available.</returns>
        public bool TryAllocate(uint count, out uint firstVertex)
        {
            for (var index = 0; index < _freeRanges.Count; index++)
            {
                var range = _freeRanges[index];
                if (range.Count < count)
                    continue;
                firstVertex = range.First;
                if (range.Count == count)
                    _freeRanges.RemoveAt(index);
                else
                    _freeRanges[index] = new FreeRange(range.First + count, range.Count - count);
                return true;
            }
            firstVertex = 0;
            return false;
        }

        /// <summary>Returns a range and coalesces adjacent free space.</summary>
        /// <param name="firstVertex">First returned vertex.</param>
        /// <param name="count">Returned vertex count.</param>
        public void Release(uint firstVertex, uint count)
        {
            _freeRanges.Add(new FreeRange(firstVertex, count));
            _freeRanges.Sort(static (left, right) => left.First.CompareTo(right.First));
            for (var index = _freeRanges.Count - 1; index > 0; index--)
            {
                var left = _freeRanges[index - 1];
                var right = _freeRanges[index];
                if (left.First + left.Count != right.First)
                    continue;
                _freeRanges[index - 1] = new FreeRange(left.First, left.Count + right.Count);
                _freeRanges.RemoveAt(index);
            }
        }

        /// <summary>Destroys this page.</summary>
        /// <param name="vk">Vulkan API.</param>
        /// <param name="device">Logical device.</param>
        public void Destroy(Vk vk, Device device)
        {
            vk.DestroyBuffer(device, Buffer, null);
            vk.FreeMemory(device, Memory, null);
        }

        /// <summary>Describes free space within a page.</summary>
        /// <param name="First">First free vertex.</param>
        /// <param name="Count">Free vertex count.</param>
        private readonly record struct FreeRange(uint First, uint Count);
    }
}
