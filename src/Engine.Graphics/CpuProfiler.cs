using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Engine.Graphics;

/// <summary>Captures instrumented managed method calls for one engine frame.</summary>
public static class CpuProfiler
{
    private const int MaximumEventsPerThread = 65_536;
    private static readonly object ThreadCaptureLock = new();
    private static readonly List<ThreadCapture> ThreadCaptures = [];
    [ThreadStatic]
    private static ThreadCapture? _currentThreadCapture;
    private static int _enabled;
    private static int _recording;
    private static int _frameGeneration;
    private static ThreadCapture? _frameThreadCapture;

    /// <summary>Gets or sets whether managed method instrumentation is enabled.</summary>
    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set
        {
            Volatile.Write(ref _enabled, value ? 1 : 0);
            if (!value)
                Volatile.Write(ref _recording, 0);
        }
    }

    /// <summary>Begins recording instrumented calls on all participating managed threads.</summary>
    public static void BeginFrame()
    {
        if (!Enabled)
            return;
        Volatile.Write(ref _recording, 0);
        var frameThread = GetOrCreateThreadCapture();
        _frameThreadCapture = frameThread;
        var generation = Interlocked.Increment(ref _frameGeneration);
        lock (ThreadCaptureLock)
        {
            for (var index = 0; index < ThreadCaptures.Count; index++)
                ThreadCaptures[index].Reset(generation);
        }
        Volatile.Write(ref _recording, 1);
    }

    /// <summary>Records entry into a build-instrumented method.</summary>
    /// <param name="methodName">Stable assembly-qualified display name embedded by the instrumenter.</param>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Enter(string methodName)
    {
        if (Volatile.Read(ref _recording) == 0)
            return;
        Append(Volatile.Read(ref _frameGeneration), new HookEvent(
            HookEventKind.Enter,
            methodName,
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread()));
    }

    /// <summary>Records exit from a build-instrumented method.</summary>
    /// <param name="methodName">Stable method name used to recover from exceptional unwinds.</param>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Leave(string methodName)
    {
        if (Volatile.Read(ref _recording) == 0)
            return;
        Append(Volatile.Read(ref _frameGeneration), new HookEvent(
            HookEventKind.Leave,
            methodName,
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread()));
    }

    /// <summary>Records entry into an explicitly identified blocking operation.</summary>
    /// <param name="waitName">Stable display name for the wait operation.</param>
    public static void EnterWait(string waitName)
    {
        if (Volatile.Read(ref _recording) == 0)
            return;
        Append(Volatile.Read(ref _frameGeneration), new HookEvent(
            HookEventKind.WaitEnter,
            waitName,
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread()));
    }

    /// <summary>Records exit from an explicitly identified blocking operation.</summary>
    /// <param name="waitName">Stable display name used when the wait was entered.</param>
    public static void LeaveWait(string waitName)
    {
        if (Volatile.Read(ref _recording) == 0)
            return;
        Append(Volatile.Read(ref _frameGeneration), new HookEvent(
            HookEventKind.WaitLeave,
            waitName,
            Stopwatch.GetTimestamp(),
            GC.GetAllocatedBytesForCurrentThread()));
    }

    /// <summary>Stops recording and builds the exact call-path tree for the frame.</summary>
    /// <returns>Pre-order call-tree rows containing CPU and allocation measurements.</returns>
    public static CpuProfileMarker[] EndFrame()
    {
        Volatile.Write(ref _recording, 0);
        ThreadCapture[] captures;
        lock (ThreadCaptureLock)
            captures = ThreadCaptures.ToArray();

        var markers = new List<CpuProfileMarker>();
        if (_frameThreadCapture is { } frameThread)
            AppendThreadTree(frameThread, markers);
        for (var index = 0; index < captures.Length; index++)
        {
            if (!ReferenceEquals(captures[index], _frameThreadCapture))
                AppendThreadTree(captures[index], markers);
        }
        return markers.ToArray();
    }

    /// <summary>Appends an event to the current thread's fixed frame buffer.</summary>
    /// <param name="generation">Active frame generation.</param>
    /// <param name="hookEvent">Event to append.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Append(int generation, HookEvent hookEvent)
    {
        var capture = _currentThreadCapture ?? GetOrCreateThreadCapture();
        capture.Append(generation, hookEvent);
    }

    /// <summary>Gets or registers the calling thread's reusable capture buffer.</summary>
    /// <returns>Capture buffer owned by the calling thread.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ThreadCapture GetOrCreateThreadCapture()
    {
        if (_currentThreadCapture is { } existing)
            return existing;
        var capture = new ThreadCapture(
            Environment.CurrentManagedThreadId,
            Thread.CurrentThread.Name);
        capture.Reset(Volatile.Read(ref _frameGeneration));
        lock (ThreadCaptureLock)
            ThreadCaptures.Add(capture);
        _currentThreadCapture = capture;
        return capture;
    }

    /// <summary>Aggregates one thread's nested events beneath a synthetic thread root.</summary>
    /// <param name="capture">Thread capture to aggregate.</param>
    /// <param name="markers">Destination flat marker list.</param>
    private static void AppendThreadTree(ThreadCapture capture, List<CpuProfileMarker> markers)
    {
        var events = capture.WrittenEvents;
        if (events.IsEmpty)
            return;

        var roots = new List<CallTreeNode>();
        var calls = new Stack<OpenCall>();
        var frameEndTimestamp = events[^1].Timestamp;
        var frameEndAllocation = events[^1].AllocatedBytes;
        foreach (var hookEvent in events)
        {
            if (hookEvent.Kind is HookEventKind.Enter or HookEventKind.WaitEnter
                && hookEvent.MethodName is not null)
            {
                var siblings = calls.TryPeek(out var parent) ? parent.Node.Children : roots;
                var node = siblings.FirstOrDefault(candidate => candidate.Name == hookEvent.MethodName);
                if (node is null)
                {
                    node = new CallTreeNode(
                        hookEvent.MethodName,
                        hookEvent.Kind == HookEventKind.WaitEnter);
                    siblings.Add(node);
                }
                node.CallCount++;
                calls.Push(new OpenCall(node, hookEvent.Timestamp, hookEvent.AllocatedBytes));
            }
            else if (hookEvent.Kind is HookEventKind.Leave or HookEventKind.WaitLeave
                && calls.Count > 0)
            {
                CloseThroughMethod(
                    calls, hookEvent.MethodName, hookEvent.Timestamp, hookEvent.AllocatedBytes);
            }
        }
        while (calls.Count > 0)
            CloseCall(calls, frameEndTimestamp, frameEndAllocation);

        var totalElapsedTicks = 0L;
        var totalWaitTicks = 0L;
        var allocatedBytes = 0L;
        for (var index = 0; index < roots.Count; index++)
        {
            totalElapsedTicks += roots[index].TotalElapsedTicks;
            totalWaitTicks += roots[index].TotalWaitTicks;
            allocatedBytes += roots[index].AllocatedBytes;
        }
        var threadIndex = markers.Count;
        markers.Add(new CpuProfileMarker(
            capture.GetDisplayName(ReferenceEquals(capture, _frameThreadCapture)),
            -1,
            0,
            totalElapsedTicks * 1000d / Stopwatch.Frequency,
            0d,
            allocatedBytes,
            0L,
            1,
            totalWaitTicks * 1000d / Stopwatch.Frequency,
            0d));
        foreach (var root in roots.OrderByDescending(node => node.TotalElapsedTicks))
            Flatten(root, threadIndex, 1, markers);
    }

    /// <summary>Closes through a named caller, recovering calls unwound by an exception.</summary>
    /// <param name="calls">Active invocation stack.</param>
    /// <param name="methodName">Method whose normal return was instrumented.</param>
    /// <param name="endTimestamp">Return timestamp.</param>
    /// <param name="endAllocation">Allocated-byte counter at return.</param>
    private static void CloseThroughMethod(
        Stack<OpenCall> calls,
        string? methodName,
        long endTimestamp,
        long endAllocation)
    {
        while (calls.Count > 0)
        {
            var isMatch = calls.Peek().Node.Name == methodName;
            CloseCall(calls, endTimestamp, endAllocation);
            if (isMatch)
                return;
        }
    }

    /// <summary>Closes one invocation and attributes child CPU and allocation totals.</summary>
    /// <param name="calls">Active invocation stack.</param>
    /// <param name="endTimestamp">Exit timestamp.</param>
    /// <param name="endAllocation">Current-thread allocated-byte counter at exit.</param>
    private static void CloseCall(
        Stack<OpenCall> calls,
        long endTimestamp,
        long endAllocation)
    {
        var call = calls.Pop();
        var elapsedTicks = Math.Max(0L, endTimestamp - call.StartTimestamp);
        var allocated = Math.Max(0L, endAllocation - call.StartAllocation);
        var selfElapsedTicks = Math.Max(0L, elapsedTicks - call.ChildElapsedTicks);
        var selfWaitTicks = call.Node.IsWait ? selfElapsedTicks : 0L;
        var totalWaitTicks = call.ChildWaitTicks + selfWaitTicks;
        call.Node.TotalElapsedTicks += elapsedTicks;
        call.Node.SelfElapsedTicks += selfElapsedTicks;
        call.Node.TotalWaitTicks += totalWaitTicks;
        call.Node.SelfWaitTicks += selfWaitTicks;
        call.Node.AllocatedBytes += allocated;
        call.Node.SelfAllocatedBytes += Math.Max(0L, allocated - call.ChildAllocatedBytes);
        if (calls.TryPeek(out var parent))
        {
            parent.ChildElapsedTicks += elapsedTicks;
            parent.ChildWaitTicks += totalWaitTicks;
            parent.ChildAllocatedBytes += allocated;
        }
    }

    /// <summary>Flattens a call-path branch for the profiler UI.</summary>
    /// <param name="node">Node to emit.</param>
    /// <param name="parentIndex">Parent row index.</param>
    /// <param name="depth">Tree depth.</param>
    /// <param name="markers">Destination rows.</param>
    private static void Flatten(CallTreeNode node, int parentIndex, int depth, List<CpuProfileMarker> markers)
    {
        var index = markers.Count;
        markers.Add(new CpuProfileMarker(
            node.Name,
            parentIndex,
            depth,
            node.TotalElapsedTicks * 1000d / Stopwatch.Frequency,
            node.SelfElapsedTicks * 1000d / Stopwatch.Frequency,
            node.AllocatedBytes,
            node.SelfAllocatedBytes,
            node.CallCount,
            node.TotalWaitTicks * 1000d / Stopwatch.Frequency,
            node.SelfWaitTicks * 1000d / Stopwatch.Frequency));
        foreach (var child in node.Children.OrderByDescending(candidate => candidate.TotalElapsedTicks))
            Flatten(child, index, depth + 1, markers);
    }

    private enum HookEventKind
    {
        Enter,
        Leave,
        WaitEnter,
        WaitLeave
    }

    private readonly record struct HookEvent(
        HookEventKind Kind,
        string? MethodName,
        long Timestamp,
        long AllocatedBytes);

    private sealed class ThreadCapture
    {
        private readonly HookEvent[] _events = new HookEvent[MaximumEventsPerThread];
        private readonly int _threadId;
        private readonly string? _threadName;
        private int _count;
        private int _generation;

        /// <summary>Creates a reusable event buffer owned by one managed thread.</summary>
        /// <param name="threadId">Managed thread identifier.</param>
        /// <param name="threadName">Optional managed thread name.</param>
        internal ThreadCapture(int threadId, string? threadName)
        {
            _threadId = threadId;
            _threadName = threadName;
        }

        /// <summary>Gets the events published by the owning thread.</summary>
        internal ReadOnlySpan<HookEvent> WrittenEvents =>
            _events.AsSpan(0, Math.Min(Volatile.Read(ref _count), MaximumEventsPerThread));

        /// <summary>Resets this buffer for a new frame generation.</summary>
        /// <param name="generation">New frame generation.</param>
        internal void Reset(int generation)
        {
            Volatile.Write(ref _count, 0);
            Volatile.Write(ref _generation, generation);
        }

        /// <summary>Publishes one event when this buffer belongs to the active frame.</summary>
        /// <param name="generation">Event frame generation.</param>
        /// <param name="hookEvent">Event to publish.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Append(int generation, HookEvent hookEvent)
        {
            if (Volatile.Read(ref _generation) != generation)
                return;
            var index = _count;
            if ((uint)index >= MaximumEventsPerThread)
                return;
            _events[index] = hookEvent;
            Volatile.Write(ref _count, index + 1);
        }

        /// <summary>Builds a stable label for the profiler thread root.</summary>
        /// <param name="isFrameThread">Whether this thread began the frame.</param>
        /// <returns>Thread name and managed identifier.</returns>
        internal string GetDisplayName(bool isFrameThread)
        {
            if (isFrameThread)
                return $"Main Thread [{_threadId}]";
            return string.IsNullOrWhiteSpace(_threadName)
                ? $"Worker Thread [{_threadId}]"
                : $"{_threadName} [{_threadId}]";
        }
    }

    private sealed class OpenCall
    {
        /// <summary>Creates an active method invocation.</summary>
        /// <param name="node">Aggregated call-path node.</param>
        /// <param name="startTimestamp">Entry timestamp.</param>
        /// <param name="startAllocation">Allocated bytes at entry.</param>
        internal OpenCall(CallTreeNode node, long startTimestamp, long startAllocation)
        {
            Node = node;
            StartTimestamp = startTimestamp;
            StartAllocation = startAllocation;
        }

        internal CallTreeNode Node { get; }
        internal long StartTimestamp { get; }
        internal long StartAllocation { get; }
        internal long ChildElapsedTicks { get; set; }
        internal long ChildWaitTicks { get; set; }
        internal long ChildAllocatedBytes { get; set; }
    }

    private sealed class CallTreeNode
    {
        /// <summary>Creates one aggregated call-path node.</summary>
        /// <param name="name">Method display name.</param>
        /// <param name="isWait">Whether this node represents an explicitly marked wait.</param>
        internal CallTreeNode(string name, bool isWait)
        {
            Name = name;
            IsWait = isWait;
        }

        internal string Name { get; }
        internal bool IsWait { get; }
        internal List<CallTreeNode> Children { get; } = [];
        internal long TotalElapsedTicks { get; set; }
        internal long SelfElapsedTicks { get; set; }
        internal long TotalWaitTicks { get; set; }
        internal long SelfWaitTicks { get; set; }
        internal long AllocatedBytes { get; set; }
        internal long SelfAllocatedBytes { get; set; }
        internal int CallCount { get; set; }
    }
}
