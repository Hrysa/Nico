namespace Engine.Graphics;

/// <summary>Describes one method in a captured frame's exhaustive call tree.</summary>
/// <param name="Name">Resolved method display name.</param>
/// <param name="ParentIndex">Parent method index, or minus one for a root method.</param>
/// <param name="Depth">Hierarchy indentation depth.</param>
/// <param name="TotalMilliseconds">Inclusive wall-clock duration.</param>
/// <param name="SelfMilliseconds">Wall-clock duration attributed directly to this method.</param>
/// <param name="GcAllocatedBytes">Inclusive managed allocation attributed to this method.</param>
/// <param name="SelfGcAllocatedBytes">Managed allocation attributed directly to this method.</param>
/// <param name="SampleCount">Number of method invocations represented by this call path.</param>
/// <param name="WaitMilliseconds">Inclusive duration inside explicitly marked waits.</param>
/// <param name="SelfWaitMilliseconds">Wait duration attributed directly to this call.</param>
public readonly record struct CpuProfileMarker(
    string Name,
    int ParentIndex,
    int Depth,
    double TotalMilliseconds,
    double SelfMilliseconds,
    long GcAllocatedBytes,
    long SelfGcAllocatedBytes,
    int SampleCount = 0,
    double WaitMilliseconds = 0d,
    double SelfWaitMilliseconds = 0d);
