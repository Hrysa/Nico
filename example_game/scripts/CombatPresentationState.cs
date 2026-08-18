using ExampleGame.Networking;

namespace ExampleGame;

/// <summary>Shares the newest authoritative combat snapshot between presentation scripts.</summary>
internal static class CombatPresentationState
{
    /// <summary>Gets whether an authenticated combat snapshot has arrived.</summary>
    internal static bool HasSnapshot { get; private set; }

    /// <summary>Gets the newest authenticated player and monster state.</summary>
    internal static ServerSnapshotMessage Snapshot { get; private set; }

    /// <summary>Publishes one newer authenticated snapshot to local presentation.</summary>
    /// <param name="snapshot">Newest server snapshot.</param>
    internal static void Publish(ServerSnapshotMessage snapshot)
    {
        Snapshot = snapshot;
        HasSnapshot = true;
    }

    /// <summary>Clears retained combat state when the local player disconnects.</summary>
    internal static void Clear()
    {
        Snapshot = default;
        HasSnapshot = false;
    }
}
