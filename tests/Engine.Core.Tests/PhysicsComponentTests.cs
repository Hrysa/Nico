using System.Numerics;
using Engine.Core;
using Xunit;

namespace Engine.Core.Tests;

public sealed class PhysicsComponentTests
{
    /// <summary>Rejects ambiguous multi-bit layer membership while allowing any query mask.</summary>
    [Fact]
    public void CollisionLayer_MultipleBits_ThrowsArgumentOutOfRangeException()
    {
        var collider = new BoxColliderComponent();

        Assert.Throws<ArgumentOutOfRangeException>(() => collider.CollisionLayer = 3u);
        collider.CollisionLayer = 8u;
        collider.CollisionMask = 3u;

        Assert.Equal(8u, collider.CollisionLayer);
        Assert.Equal(3u, collider.CollisionMask);
    }

    /// <summary>Publishes component-value notifications for attached collider edits.</summary>
    [Fact]
    public void ColliderPropertyChange_AttachedNode_NotifiesComponentValues()
    {
        var node = new Node();
        var collider = new SphereColliderComponent();
        node.AddComponent(collider);
        var changes = NodeChangeKind.None;
        node.Changed += kind => changes |= kind;

        collider.Center = new Vector3(1f, 2f, 3f);
        collider.Radius = 2f;
        collider.IsTrigger = true;

        Assert.True((changes & NodeChangeKind.ComponentValues) != 0);
    }

    /// <summary>Rejects nonfinite collider centers before they reach serialization or physics.</summary>
    [Fact]
    public void Center_NonFinite_ThrowsArgumentOutOfRangeException()
    {
        var collider = new BoxColliderComponent();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            collider.Center = new Vector3(float.NaN, 0f, 0f));
    }
}
