namespace Engine.Graphics;

/// <summary>Describes one method in a captured frame's exhaustive call tree.</summary>
/// <param name="Name">Resolved method display name.</param>
/// <param name="ParentIndex">Parent method index, or minus one for a root method.</param>
/// <param name="Depth">Hierarchy indentation depth.</param>
/// <param name="TotalMilliseconds">Inclusive instrumented method duration.</param>
/// <param name="SelfMilliseconds">Instrumented duration attributed directly to this method.</param>
/// <param name="GcAllocatedBytes">Inclusive managed allocation attributed to this method.</param>
/// <param name="SelfGcAllocatedBytes">Managed allocation attributed directly to this method.</param>
/// <param name="SampleCount">Number of method invocations represented by this call path.</param>
public readonly record struct CpuProfileMarker(
    string Name,
    int ParentIndex,
    int Depth,
    double TotalMilliseconds,
    double SelfMilliseconds,
    long GcAllocatedBytes,
    long SelfGcAllocatedBytes,
    int SampleCount = 0);
