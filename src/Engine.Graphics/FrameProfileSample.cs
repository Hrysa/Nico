namespace Engine.Graphics;

/// <summary>Describes managed CPU work and allocation observed for one rendered frame.</summary>
/// <param name="FrameNumber">Monotonic rendered-frame number.</param>
/// <param name="CpuMilliseconds">Combined update and render CPU duration.</param>
/// <param name="UpdateMilliseconds">Managed update callback duration.</param>
/// <param name="RenderMilliseconds">Render submission duration.</param>
/// <param name="GcAllocatedBytes">Managed bytes allocated on the profiled thread between update start and render completion.</param>
/// <param name="CpuMarkers">Captured pre-order instrumented method hierarchy.</param>
public readonly record struct FrameProfileSample(
    ulong FrameNumber,
    double CpuMilliseconds,
    double UpdateMilliseconds,
    double RenderMilliseconds,
    long GcAllocatedBytes,
    CpuProfileMarker[]? CpuMarkers = null);
