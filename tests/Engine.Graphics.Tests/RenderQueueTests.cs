using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>Tests retained mesh submissions.</summary>
public sealed class RenderQueueTests
{
    /// <summary>Verifies a queue stores a mesh identity instead of copying vertex arrays.</summary>
    [Fact]
    public void Add_ValidMesh_StoresHandleAndTransforms()
    {
        var queue = new RenderQueue();
        var handle = new MeshHandle(42);
        var pushConstants = new PushConstants { Model = Matrix4x4.Identity };

        queue.Add(handle, pushConstants);

        var command = Assert.Single(queue.Commands);
        Assert.Equal(handle, command.Mesh);
        Assert.Equal(Matrix4x4.Identity, command.PushConstants.Model);
    }

    /// <summary>Verifies the invalid default handle cannot enter a render queue.</summary>
    [Fact]
    public void Add_DefaultMesh_Throws()
    {
        var queue = new RenderQueue();

        Assert.Throws<ArgumentException>(() => queue.Add(default, default));
    }

    /// <summary>Stores the palette identity required by a skinned draw.</summary>
    [Fact]
    public void AddSkinned_ValidHandles_StoresPalette()
    {
        var queue = new RenderQueue();
        var mesh = new MeshHandle(42);
        var palette = new SkinPaletteHandle(7);

        queue.AddSkinned(mesh, palette, default);

        var command = Assert.Single(queue.Commands);
        Assert.Equal(mesh, command.Mesh);
        Assert.Equal(palette, command.SkinPalette);
    }

    /// <summary>Verifies repeated command enumeration through the hot-path span does not allocate.</summary>
    [Fact]
    public void CommandSpan_RepeatedEnumerationDoesNotAllocate()
    {
        var queue = new RenderQueue();
        queue.Add(new MeshHandle(42), default);
        var warmup = queue.CommandSpan[0].Mesh.Value;
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        ulong sum = 0;
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            foreach (var command in queue.CommandSpan)
                sum += command.Mesh.Value;
        }

        var allocationEnd = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(42UL, warmup);
        Assert.Equal(420_000UL, sum);
        Assert.Equal(allocationStart, allocationEnd);
    }
}
