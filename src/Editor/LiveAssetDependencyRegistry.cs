using Engine.Core;

namespace Editor;

/// <summary>Tracks live editor objects that must rebuild after an asset is republished.</summary>
public sealed class LiveAssetDependencyRegistry
{
    private readonly Dictionary<object, Registration> _registrations =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Adds or replaces the asset dependency owned by one live object.</summary>
    /// <param name="owner">Object whose lifetime owns the dependency.</param>
    /// <param name="asset">Persistent asset that supplies the live value.</param>
    /// <param name="refresh">Callback that rebuilds the value from current artifacts.</param>
    public void Bind(object owner, AssetId asset, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(refresh);
        _registrations[owner] = new Registration(asset, refresh);
    }

    /// <summary>Removes the dependency owned by one live object.</summary>
    /// <param name="owner">Object leaving the live editor scene.</param>
    /// <returns>True when a dependency was removed.</returns>
    public bool Unbind(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _registrations.Remove(owner);
    }

    /// <summary>Refreshes every live object that depends on a republished asset.</summary>
    /// <param name="asset">Persistent asset whose published generation changed.</param>
    public void Refresh(AssetId asset)
    {
        foreach (var registration in _registrations.Values)
        {
            if (registration.Asset == asset)
                registration.Refresh();
        }
    }

    private readonly record struct Registration(AssetId Asset, Action Refresh);
}
