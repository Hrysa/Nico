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
}
