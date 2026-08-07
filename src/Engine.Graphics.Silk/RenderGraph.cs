using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>
/// Manages multiple render passes with separate CommandBuffers and Silk.NET.Vulkan.Semaphore synchronization.
/// Each pass records into its own CommandBuffer; passes are submitted in order with semaphores
/// ensuring GPU-side dependencies without DeviceWaitIdle.
/// </summary>
internal unsafe class FrameScheduler
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _graphicsQueue;

    private const uint MaxFramesInFlight = 2;

    // Per-frame resources
    private readonly CommandPool[] _commandPools = new CommandPool[MaxFramesInFlight];
    private readonly Fence[] _inFlightFences = new Fence[MaxFramesInFlight];

    // Per-pass resources (allocated from the per-frame command pool)
    private const int MaxPasses = 4;
    private readonly Silk.NET.Vulkan.Semaphore[,] _passSemaphores =
        new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight, MaxPasses];

    private uint _currentFrame;
    private uint _passCount;

    public FrameScheduler(Vk vk, Device device, Queue graphicsQueue, uint queueFamilyIndex)
    {
        _vk = vk;
        _device = device;
        _graphicsQueue = graphicsQueue;

        // Create per-frame command pools and fences
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = queueFamilyIndex
            };
            _vk.CreateCommandPool(_device, &poolInfo, null, out _commandPools[i]);

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit
            };
            _vk.CreateFence(_device, &fenceInfo, null, out _inFlightFences[i]);
        }

        // Create semaphores for inter-pass sync
        for (var frame = 0; frame < MaxFramesInFlight; frame++)
        {
            for (var pass = 0; pass < MaxPasses; pass++)
            {
                var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
                _vk.CreateSemaphore(_device, &semInfo, null, out _passSemaphores[frame, pass]);
            }
        }
    }

    /// <summary>
    /// Begins a new frame. Waits for the previous frame's fence and returns the current frame index.
    /// </summary>
    public uint BeginFrame()
    {
        var fence = _inFlightFences[_currentFrame];
        CpuProfiler.EnterWait("Wait: Vulkan frame fence");
        try
        {
            _vk.WaitForFences(_device, 1, &fence, new Bool32(true), ulong.MaxValue);
        }
        finally
        {
            CpuProfiler.LeaveWait("Wait: Vulkan frame fence");
        }
        _vk.ResetCommandPool(_device, _commandPools[_currentFrame], 0);
        _passCount = 0;
        return _currentFrame;
    }

    /// <summary>
    /// Resets the current frame fence immediately before the submission that will signal it.
    /// </summary>
    public void PrepareCurrentFenceForSubmission()
    {
        var fence = _inFlightFences[_currentFrame];
        _vk.ResetFences(_device, 1, &fence);
    }

    /// <summary>
    /// Ends the current frame. Advances the frame counter.
    /// </summary>
    public void EndFrame()
    {
        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    /// <summary>
    /// Gets the fence for the current frame (used for present synchronization).
    /// </summary>
    public Fence GetCurrentFence() => _inFlightFences[_currentFrame];

    /// <summary>
    /// Allocates and begins a new command buffer for a render pass.
    /// Returns the semaphore that will be signaled when this pass completes on the GPU.
    /// </summary>
    public (CommandBuffer commandBuffer, Silk.NET.Vulkan.Semaphore semaphore) BeginPass()
    {
        if (_passCount >= MaxPasses)
            throw new InvalidOperationException($"Maximum {MaxPasses} passes per frame exceeded");

        var pool = _commandPools[_currentFrame];
        var sem = _passSemaphores[_currentFrame, _passCount];

        // Allocate command buffer
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        _vk.AllocateCommandBuffers(_device, &allocInfo, out var cmdBuffer);

        // Begin recording
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        _vk.BeginCommandBuffer(cmdBuffer, &beginInfo);

        _passCount++;
        return (cmdBuffer, sem);
    }

    /// <summary>
    /// Ends the current pass's command buffer.
    /// </summary>
    public void EndPass(CommandBuffer commandBuffer)
    {
        _vk.EndCommandBuffer(commandBuffer);
    }

    /// <summary>
    /// Submits a pass's command buffer to the GPU.
    /// waitSemaphore: semaphore to wait before executing (imageAvailable for first pass, previous pass's semaphore for subsequent).
    /// signalSemaphore: semaphore to signal when this pass completes (used by next pass or present).
    /// </summary>
    public void SubmitPass(CommandBuffer commandBuffer, Silk.NET.Vulkan.Semaphore waitSemaphore, Silk.NET.Vulkan.Semaphore signalSemaphore, Fence signalFence)
    {
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        var waitSems = stackalloc[] { waitSemaphore };
        var waitStages = stackalloc[] { waitStage };
        var signalSems = stackalloc[] { signalSemaphore };

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = waitSems,
            PWaitDstStageMask = waitStages,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = signalSems
        };

        _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, signalFence);
    }

    /// <summary>
    /// Destroys all frame-scheduler resources.
    /// </summary>
    public void Destroy()
    {
        _vk.DeviceWaitIdle(_device);

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            _vk.DestroyCommandPool(_device, _commandPools[i], null);
            _vk.DestroyFence(_device, _inFlightFences[i], null);
        }

        for (var frame = 0; frame < MaxFramesInFlight; frame++)
        {
            for (var pass = 0; pass < MaxPasses; pass++)
                _vk.DestroySemaphore(_device, _passSemaphores[frame, pass], null);
        }
    }
}
