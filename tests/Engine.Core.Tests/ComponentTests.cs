using Xunit;

namespace Engine.Core.Tests;

/// <summary>Verifies node component ownership and persistent script override behavior.</summary>
public sealed class ComponentTests
{
    /// <summary>Supports multiple ordered scripts while retaining the legacy first-script facade.</summary>
    [Fact]
    public void Node_MultipleScripts_PreservesOwnershipAndLegacyFacade()
    {
        var node = new Node();
        var first = new ScriptComponent(AssetId.New());
        var second = new ScriptComponent(AssetId.New());
        var changes = new List<NodeChangeKind>();
        node.Changed += changes.Add;

        node.AddComponent(first);
        node.AddComponent(second);
        second.SetPropertyOverride(17, SerializedPropertyValue.From(4d));

        Assert.Same(node, first.Owner);
        Assert.Same(node, second.Owner);
        Assert.Equal(first.ScriptId, node.ScriptId);
        Assert.Equal(2, node.Components.Count);
        Assert.Contains(NodeChangeKind.Components, changes);
        Assert.Contains(NodeChangeKind.ComponentValues, changes);

        node.ScriptId = null;

        Assert.Empty(node.Components);
        Assert.Null(first.Owner);
        Assert.Null(second.Owner);
    }

    /// <summary>Rejects attaching one component instance to more than one node.</summary>
    [Fact]
    public void AddComponent_ComponentAlreadyOwned_ThrowsInvalidOperationException()
    {
        var firstOwner = new Node();
        var secondOwner = new Node();
        var component = new ScriptComponent(AssetId.New());
        firstOwner.AddComponent(component);

        Assert.Throws<InvalidOperationException>(() => secondOwner.AddComponent(component));
    }
}
