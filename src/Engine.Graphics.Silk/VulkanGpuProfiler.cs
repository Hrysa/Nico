#if DEBUG_GRAPHICS_SILK
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Collects delayed per-frame Vulkan timestamp queries without stalling the GPU.</summary>
internal unsafe sealed class VulkanGpuProfiler
{
    private const uint QueryCount = 6;
    private const uint FrameBegin = 0;
    private const uint ViewportBegin = 1;
    private const uint ViewportEnd = 2;
    private const uint CompositionBegin = 3;
    private const uint CompositionEnd = 4;
    private const uint FrameEnd = 5;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly QueryPool[] _pools;
    private readonly ulong[] _frameNumbers;
    private readonly bool[] _submitted;
    private readonly double _millisecondsPerTick;
    private readonly ulong _timestampMask;

    /// <summary>Gets the most recently completed GPU frame measurement.</summary>
    public GpuFrameProfile? Latest { get; private set; }

    /// <summary>Consumes the most recently completed measurement.</summary>
    /// <returns>Completed GPU frame, or null when no query became available.</returns>
    public GpuFrameProfile? TakeLatest()
    {
        var latest = Latest;
        Latest = null;
        return latest;
    }

    /// <summary>Creates timestamp pools for every independently fenced frame slot.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="frameCount">Number of frames in flight.</param>
    /// <param name="timestampPeriod">Nanoseconds represented by one timestamp tick.</param>
    /// <param name="timestampValidBits">Valid low-order timestamp bits for the graphics queue.</param>
    public VulkanGpuProfiler(
        Vk vk, Device device, int frameCount, float timestampPeriod,
        uint timestampValidBits)
    {
        _vk = vk;
        _device = device;
        _pools = new QueryPool[frameCount];
        _frameNumbers = new ulong[frameCount];
        _submitted = new bool[frameCount];
        _millisecondsPerTick = timestampPeriod / 1_000_000d;
        _timestampMask = timestampValidBits >= 64
            ? ulong.MaxValue : (1UL << (int)timestampValidBits) - 1UL;
        var createInfo = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = QueryCount
        };
        for (var index = 0; index < _pools.Length; index++)
        {
            var result = _vk.CreateQueryPool(_device, &createInfo, null, out _pools[index]);
            if (result != Result.Success)
                throw new InvalidOperationException(
                    $"Failed to create GPU timestamp query pool: {result}.");
        }
    }

    /// <summary>Reads results belonging to a frame slot whose fence has already signaled.</summary>
    /// <param name="frameIndex">Reusable frame slot.</param>
    public void Collect(uint frameIndex)
    {
        if (!_submitted[frameIndex])
            return;
        var values = stackalloc ulong[(int)QueryCount];
        var result = _vk.GetQueryPoolResults(
            _device, _pools[frameIndex], 0, QueryCount,
            QueryCount * sizeof(ulong), values, sizeof(ulong), QueryResultFlags.Result64Bit);
        if (result == Result.Success)
        {
            Latest = new GpuFrameProfile(
                _frameNumbers[frameIndex],
                Elapsed(values[FrameBegin], values[FrameEnd]),
                Elapsed(values[ViewportBegin], values[ViewportEnd]),
                Elapsed(values[CompositionBegin], values[CompositionEnd]));
        }
        _submitted[frameIndex] = false;
    }

    /// <summary>Resets one pool and starts a new timestamp sequence.</summary>
    /// <param name="commandBuffer">Recording command buffer.</param>
    /// <param name="frameIndex">Current frame slot.</param>
    public void BeginFrame(CommandBuffer commandBuffer, uint frameIndex)
    {
        _vk.CmdResetQueryPool(commandBuffer, _pools[frameIndex], 0, QueryCount);
        Write(commandBuffer, frameIndex, FrameBegin, PipelineStageFlags.TopOfPipeBit);
    }

    /// <summary>Marks the beginning of offscreen viewport work.</summary>
    /// <param name="commandBuffer">Recording command buffer.</param>
    /// <param name="frameIndex">Current frame slot.</param>
    public void BeginViewport(CommandBuffer commandBuffer, uint frameIndex) =>
        Write(commandBuffer, frameIndex, ViewportBegin, PipelineStageFlags.TopOfPipeBit);

    /// <summary>Marks the end of offscreen viewport work.</summary>
    /// <param name="commandBuffer">Recording command buffer.</param>
    /// <param name="frameIndex">Current frame slot.</param>
    public void EndViewport(CommandBuffer commandBuffer, uint frameIndex) =>
        Write(commandBuffer, frameIndex, ViewportEnd, PipelineStageFlags.BottomOfPipeBit);

    /// <summary>Marks the beginning of swapchain and UI composition.</summary>
    /// <param name="commandBuffer">Recording command buffer.</param>
    /// <param name="frameIndex">Current frame slot.</param>
    public void BeginComposition(CommandBuffer commandBuffer, uint frameIndex) =>
        Write(commandBuffer, frameIndex, CompositionBegin, PipelineStageFlags.TopOfPipeBit);

    /// <summary>Marks the end of swapchain and UI composition.</summary>
    /// <param name="commandBuffer">Recording command buffer.</param>
    /// <param name="frameIndex">Current frame slot.</param>
    public void EndComposition(CommandBuffer commandBuffer, uint frameIndex) =>
        Write(commandBuffer, frameIndex, CompositionEnd, PipelineStageFlags.BottomOfPipeBit);

    /// <summary>Finishes and associates one timestamp sequence with its public frame number.</summary>
    /// <param name="commandBuffer">Recording command buffer.</param>
    /// <param name="frameIndex">Current frame slot.</param>
    /// <param name="frameNumber">Original rendered-frame number.</param>
    public void EndFrame(CommandBuffer commandBuffer, uint frameIndex, ulong frameNumber)
    {
        Write(commandBuffer, frameIndex, FrameEnd, PipelineStageFlags.BottomOfPipeBit);
        _frameNumbers[frameIndex] = frameNumber;
        _submitted[frameIndex] = true;
    }

    /// <summary>Destroys all owned query pools after the device becomes idle.</summary>
    public void Destroy()
    {
        for (var index = 0; index < _pools.Length; index++)
            _vk.DestroyQueryPool(_device, _pools[index], null);
    }

    /// <summary>Writes one timestamp into the current frame pool.</summary>
    /// <param name="commandBuffer">Recording command buffer.</param>
    /// <param name="frameIndex">Current frame slot.</param>
    /// <param name="query">Timestamp query index.</param>
    /// <param name="stage">Pipeline boundary represented by the timestamp.</param>
    private void Write(CommandBuffer commandBuffer, uint frameIndex, uint query,
        PipelineStageFlags stage)
    {
        _vk.CmdWriteTimestamp(commandBuffer, stage, _pools[frameIndex], query);
    }

    /// <summary>Converts two monotonically increasing device timestamps to milliseconds.</summary>
    /// <param name="start">Beginning device tick.</param>
    /// <param name="end">Ending device tick.</param>
    /// <returns>Elapsed milliseconds.</returns>
    private double Elapsed(ulong start, ulong end) =>
        ((end - start) & _timestampMask) * _millisecondsPerTick;
}
#endif
