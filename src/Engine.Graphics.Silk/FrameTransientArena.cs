using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Owns reusable mapped buffer pages for one-frame vertices and transfer staging.</summary>
internal unsafe sealed class FrameTransientArena
{
    private const uint DefaultPageBytes = 256 * 1024;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Func<uint, MemoryPropertyFlags, uint> _findMemoryType;
    private readonly ILogger _logger;
    private readonly List<Page>[] _pages;

    /// <summary>Creates an arena with independent pages for every frame in flight.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="frameCount">Frame slots protected by fences.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="logger">Diagnostic logger.</param>
    internal FrameTransientArena(
        Vk vk,
        Device device,
        uint frameCount,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        ILogger logger)
    {
        _vk = vk;
        _device = device;
        _findMemoryType = findMemoryType;
        _logger = logger;
        _pages = Enumerable.Range(0, checked((int)frameCount)).Select(_ => new List<Page>()).ToArray();
    }

    /// <summary>Resets a frame slot after its fence has completed.</summary>
    /// <param name="frameIndex">Completed frame slot.</param>
    internal void Reset(uint frameIndex)
    {
        foreach (var page in _pages[frameIndex])
            page.UsedBytes = 0;
    }

    /// <summary>Allocates aligned bytes valid until this frame slot is reset.</summary>
    /// <param name="frameIndex">Active frame slot.</param>
    /// <param name="byteCount">Required byte count.</param>
    /// <param name="alignment">Required byte alignment.</param>
    /// <returns>Mapped Vulkan buffer range.</returns>
    internal Allocation Allocate(uint frameIndex, uint byteCount, uint alignment = 16)
    {
        if (byteCount == 0)
            return default;
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a power of two.");
        foreach (var page in _pages[frameIndex])
        {
            var offset = Align(page.UsedBytes, alignment);
            if ((ulong)offset + byteCount > page.CapacityBytes)
                continue;
            page.UsedBytes = offset + byteCount;
            return new Allocation(page.Buffer, offset, byteCount,
                (void*)((byte*)page.MappedPointer + offset));
        }

        var capacity = Math.Max(DefaultPageBytes, RoundUpPowerOfTwo(byteCount));
        var created = CreatePage(capacity);
        _pages[frameIndex].Add(created);
        created.UsedBytes = byteCount;
        return new Allocation(created.Buffer, 0, byteCount, (void*)created.MappedPointer);
    }

    /// <summary>Destroys all frame pages.</summary>
    internal void Destroy()
    {
        foreach (var framePages in _pages)
        {
            foreach (var page in framePages)
                page.Destroy(_vk, _device);
            framePages.Clear();
        }
    }

    /// <summary>Creates one mapped page usable as both vertex data and a transfer source.</summary>
    /// <param name="capacityBytes">Page size in bytes.</param>
    /// <returns>Created page.</returns>
    private Page CreatePage(uint capacityBytes)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = capacityBytes,
            Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateBuffer(_device, &info, null, out var buffer), "create transient page");
        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        Check(_vk.AllocateMemory(_device, &allocation, null, out var memory),
            "allocate transient page memory");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "bind transient page memory");
        void* mapped;
        Check(_vk.MapMemory(_device, memory, 0, capacityBytes, 0, &mapped), "map transient page");
        _logger.LogDebug("Transient frame page capacity is {Capacity} bytes", capacityBytes);
        return new Page(buffer, memory, (nint)mapped, capacityBytes);
    }

    /// <summary>Aligns an unsigned byte offset.</summary>
    /// <param name="value">Unaligned value.</param>
    /// <param name="alignment">Power-of-two alignment.</param>
    /// <returns>Aligned value.</returns>
    private static uint Align(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    /// <summary>Rounds a byte count up to a power of two.</summary>
    /// <param name="value">Positive byte count.</param>
    /// <returns>Power-of-two page size.</returns>
    private static uint RoundUpPowerOfTwo(uint value)
    {
        if (value <= 1)
            return 1;
        var rounded = System.Numerics.BitOperations.RoundUpToPowerOf2(value);
        return rounded == 0 ? value : rounded;
    }

    /// <summary>Throws for a failed Vulkan operation.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }

    /// <summary>Describes one transient mapped range.</summary>
    internal readonly struct Allocation
    {
        internal Silk.NET.Vulkan.Buffer Buffer { get; }
        internal ulong ByteOffset { get; }
        internal uint ByteCount { get; }
        internal void* MappedPointer { get; }

        /// <summary>Creates one mapped range.</summary>
        /// <param name="buffer">Owning Vulkan buffer.</param>
        /// <param name="byteOffset">Range offset.</param>
        /// <param name="byteCount">Range size.</param>
        /// <param name="mappedPointer">Mapped range start.</param>
        internal Allocation(
            Silk.NET.Vulkan.Buffer buffer,
            ulong byteOffset,
            uint byteCount,
            void* mappedPointer)
        {
            Buffer = buffer;
            ByteOffset = byteOffset;
            ByteCount = byteCount;
            MappedPointer = mappedPointer;
        }
    }

    /// <summary>Owns one mapped transient buffer page.</summary>
    private sealed class Page
    {
        internal Silk.NET.Vulkan.Buffer Buffer { get; }
        internal DeviceMemory Memory { get; }
        internal nint MappedPointer { get; }
        internal uint CapacityBytes { get; }
        internal uint UsedBytes { get; set; }

        /// <summary>Creates a page record.</summary>
        /// <param name="buffer">Vulkan buffer.</param>
        /// <param name="memory">Bound memory.</param>
        /// <param name="mappedPointer">Persistent mapping.</param>
        /// <param name="capacityBytes">Page capacity.</param>
        internal Page(
            Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory,
            nint mappedPointer,
            uint capacityBytes)
        {
            Buffer = buffer;
            Memory = memory;
            MappedPointer = mappedPointer;
            CapacityBytes = capacityBytes;
        }

        /// <summary>Destroys this page.</summary>
        /// <param name="vk">Vulkan API.</param>
        /// <param name="device">Logical device.</param>
        internal void Destroy(Vk vk, Device device)
        {
            vk.UnmapMemory(device, Memory);
            vk.DestroyBuffer(device, Buffer, null);
            vk.FreeMemory(device, Memory, null);
        }
    }
}
