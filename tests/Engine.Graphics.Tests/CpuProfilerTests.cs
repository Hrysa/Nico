using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>Exercises runtime profiler capture state.</summary>
public sealed class CpuProfilerTests
{
    /// <summary>Verifies runtime capture can be enabled and disabled.</summary>
    [Fact]
    public void Enabled_SetValue_RoundTrips()
    {
        CpuProfiler.Enabled = true;
        Assert.True(CpuProfiler.Enabled);

        CpuProfiler.Enabled = false;
        Assert.False(CpuProfiler.Enabled);
    }

    /// <summary>Verifies nested hooks report inclusive/self CPU, allocations, and call counts.</summary>
    [Fact]
    public void Hooks_NestedAllocations_BuildExactTree()
    {
        CpuProfiler.Enabled = true;
        try
        {
            CpuProfiler.BeginFrame();
            CpuProfiler.Enter("Root.Run()");
            var rootAllocation = new byte[1024];
            CpuProfiler.Enter("Child.Allocate()");
            var childAllocation = new byte[2048];
            CpuProfiler.Leave("Child.Allocate()");
            CpuProfiler.Leave("Root.Run()");
            GC.KeepAlive(rootAllocation);
            GC.KeepAlive(childAllocation);

            var tree = CpuProfiler.EndFrame();

            Assert.Equal(3, tree.Length);
            Assert.StartsWith("Main Thread [", tree[0].Name);
            Assert.Equal("Root.Run()", tree[1].Name);
            Assert.Equal("Child.Allocate()", tree[2].Name);
            Assert.Equal(0, tree[1].ParentIndex);
            Assert.Equal(1, tree[2].ParentIndex);
            Assert.True(tree[1].TotalMilliseconds >= tree[2].TotalMilliseconds);
            Assert.True(tree[1].GcAllocatedBytes >= tree[2].GcAllocatedBytes);
            Assert.True(tree[2].GcAllocatedBytes >= 2048);
            Assert.True(tree[1].SelfGcAllocatedBytes >= 1024);
            Assert.Equal(1, tree[1].SampleCount);
            Assert.Equal(1, tree[2].SampleCount);
        }
        finally
        {
            CpuProfiler.Enabled = false;
        }
    }

    /// <summary>Verifies hooks from a worker thread are retained under a separate thread root.</summary>
    [Fact]
    public async Task Hooks_WorkerThread_BuildsSeparateThreadTree()
    {
        CpuProfiler.Enabled = true;
        try
        {
            CpuProfiler.BeginFrame();
            CpuProfiler.Enter("Main.Run()");
            CpuProfiler.Leave("Main.Run()");
            await Task.Run(() =>
            {
                CpuProfiler.Enter("Worker.Run()");
                var allocation = new byte[4096];
                CpuProfiler.Leave("Worker.Run()");
                GC.KeepAlive(allocation);
            });

            var tree = CpuProfiler.EndFrame();

            var mainRoot = Assert.Single(tree, marker => marker.Name.StartsWith("Main Thread ["));
            var workerMethod = Assert.Single(tree, marker => marker.Name == "Worker.Run()");
            var workerRoot = tree[workerMethod.ParentIndex];
            Assert.False(workerRoot.Name.StartsWith("Main Thread [", StringComparison.Ordinal));
            Assert.Equal(-1, workerRoot.ParentIndex);
            Assert.Equal(1, workerMethod.Depth);
            Assert.True(workerMethod.GcAllocatedBytes >= 4096);
            Assert.Equal(-1, mainRoot.ParentIndex);
        }
        finally
        {
            CpuProfiler.Enabled = false;
        }
    }

    /// <summary>Verifies a blocked method reports elapsed time without charging it as CPU work.</summary>
    [Fact]
    public void Hooks_BlockingWait_SeparatesElapsedAndCpuTime()
    {
        CpuProfiler.Enabled = true;
        try
        {
            CpuProfiler.BeginFrame();
            CpuProfiler.Enter("Root.Run()");
            CpuProfiler.EnterWait("Wait: test sleep");
            Thread.Sleep(30);
            CpuProfiler.LeaveWait("Wait: test sleep");
            CpuProfiler.Leave("Root.Run()");

            var tree = CpuProfiler.EndFrame();
            var root = Assert.Single(tree, candidate => candidate.Name == "Root.Run()");
            var wait = Assert.Single(tree, candidate => candidate.Name == "Wait: test sleep");

            Assert.True(wait.TotalMilliseconds >= 20d);
            Assert.True(wait.WaitMilliseconds >= 20d);
            Assert.True(root.WaitMilliseconds >= wait.WaitMilliseconds);
        }
        finally
        {
            CpuProfiler.Enabled = false;
        }
    }
}
