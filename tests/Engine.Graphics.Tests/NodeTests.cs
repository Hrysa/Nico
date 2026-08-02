using System.Numerics;
using Engine.Core;
using Xunit;

namespace Engine.Graphics.Tests;

public class NodeTests
{
    /// <summary>Verifies that scene graph cycles are rejected.</summary>
    [Fact]
    public void AddChild_RejectsCycle()
    {
        var root = new Node();
        var child = new Node();
        root.AddChild(child);

        Assert.Throws<InvalidOperationException>(() => child.AddChild(root));
    }

    /// <summary>Verifies that adding the same child twice is idempotent.</summary>
    [Fact]
    public void AddChild_SameParent_DoesNotDuplicateChild()
    {
        var root = new Node();
        var child = new Node();

        root.AddChild(child);
        root.AddChild(child);

        Assert.Single(root.Children);
    }

    /// <summary>Verifies that 3D child transforms include their parent transform.</summary>
    [Fact]
    public void GetModelMatrix_IncludesParentTransform()
    {
        var parent = new Node3D { Position = new Vector3(10f, 0f, 0f) };
        var child = new Node3D { Position = new Vector3(2f, 0f, 0f) };
        parent.AddChild(child);

        var worldOrigin = Vector3.Transform(Vector3.Zero, child.GetModelMatrix());

        Assert.Equal(new Vector3(12f, 0f, 0f), worldOrigin);
    }

    /// <summary>Verifies world-space editing preserves a child's parent-relative transform.</summary>
    [Fact]
    public void SetWorldTransform_ParentedChild_ConvertsToLocalPosition()
    {
        var parent = new Node3D { Position = new Vector3(10f, 0f, 0f) };
        var child = new Node3D();
        parent.AddChild(child);

        child.SetWorldTransform(new Vector3(14f, 2f, 0f), Vector3.Zero);

        Assert.Equal(new Vector3(4f, 2f, 0f), child.Position);
        Assert.Equal(new Vector3(14f, 2f, 0f), child.GetWorldPosition());
    }
}
