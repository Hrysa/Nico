using Engine.Core;
using Xunit;

namespace Editor.Tests;

public sealed class LiveAssetDependencyRegistryTests
{
    /// <summary>Refreshes every registered consumer without knowing its asset content type.</summary>
    [Fact]
    public void Refresh_MatchingAsset_NotifiesDifferentConsumerKinds()
    {
        var registry = new LiveAssetDependencyRegistry();
        var asset = AssetId.New();
        var materialConsumer = new object();
        var animationConsumer = new object();
        var materialRefreshes = 0;
        var animationRefreshes = 0;
        registry.Bind(materialConsumer, asset, () => materialRefreshes++);
        registry.Bind(animationConsumer, asset, () => animationRefreshes++);

        registry.Refresh(asset);

        Assert.Equal(1, materialRefreshes);
        Assert.Equal(1, animationRefreshes);
    }

    /// <summary>Rebinding an owner replaces its former dependency and callback.</summary>
    [Fact]
    public void Bind_ExistingOwner_ReplacesRegistration()
    {
        var registry = new LiveAssetDependencyRegistry();
        var owner = new object();
        var previousAsset = AssetId.New();
        var currentAsset = AssetId.New();
        var refreshes = 0;
        registry.Bind(owner, previousAsset, () => refreshes += 10);
        registry.Bind(owner, currentAsset, () => refreshes++);

        registry.Refresh(previousAsset);
        registry.Refresh(currentAsset);

        Assert.Equal(1, refreshes);
    }
}
