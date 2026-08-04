using Engine.Core;

namespace Engine.Assets;

/// <summary>Tracks direct asset dependencies and reverse transitive invalidation.</summary>
public sealed class AssetDependencyGraph
{
    private readonly Dictionary<AssetId, HashSet<AssetId>> _dependencies = new();
    private readonly Dictionary<AssetId, HashSet<AssetId>> _dependents = new();
    private readonly object _sync = new();

    /// <summary>Replaces the declared dependencies of one imported asset.</summary>
    /// <param name="asset">Imported asset identity.</param>
    /// <param name="dependencies">Current persistent dependencies.</param>
    public void Update(AssetId asset, IEnumerable<AssetReference> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var next = dependencies.Select(reference => reference.Asset).ToHashSet();
        lock (_sync)
        {
            if (_dependencies.TryGetValue(asset, out var previous))
            {
                foreach (var dependency in previous)
                {
                    if (_dependents.TryGetValue(dependency, out var reverse))
                    {
                        reverse.Remove(asset);
                        if (reverse.Count == 0)
                            _dependents.Remove(dependency);
                    }
                }
            }
            _dependencies[asset] = next;
            foreach (var dependency in next)
            {
                if (!_dependents.TryGetValue(dependency, out var reverse))
                {
                    reverse = [];
                    _dependents.Add(dependency, reverse);
                }
                reverse.Add(asset);
            }
        }
    }

    /// <summary>Gets the direct dependencies declared by one asset.</summary>
    /// <param name="asset">Asset identity.</param>
    /// <returns>Direct dependency identities in deterministic order.</returns>
    public IReadOnlyList<AssetId> GetDependencies(AssetId asset)
    {
        lock (_sync)
            return Snapshot(_dependencies.GetValueOrDefault(asset));
    }

    /// <summary>Gets assets that directly depend on one asset.</summary>
    /// <param name="asset">Dependency identity.</param>
    /// <returns>Direct dependent identities in deterministic order.</returns>
    public IReadOnlyList<AssetId> GetDependents(AssetId asset)
    {
        lock (_sync)
            return Snapshot(_dependents.GetValueOrDefault(asset));
    }

    /// <summary>Gets every direct and indirect dependent requiring invalidation.</summary>
    /// <param name="asset">Changed dependency identity.</param>
    /// <returns>Transitive dependent identities without the input asset.</returns>
    public IReadOnlyList<AssetId> GetTransitiveDependents(AssetId asset)
    {
        lock (_sync)
        {
            var visited = new HashSet<AssetId> { asset };
            var pending = new Queue<AssetId>();
            pending.Enqueue(asset);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!_dependents.TryGetValue(current, out var reverse))
                    continue;
                foreach (var dependent in reverse)
                {
                    if (visited.Add(dependent))
                        pending.Enqueue(dependent);
                }
            }
            visited.Remove(asset);
            return visited.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Finds dependency cycles using the current graph snapshot.</summary>
    /// <returns>Distinct cycles represented by their deterministic member sets.</returns>
    public IReadOnlyList<IReadOnlyList<AssetId>> FindCycles()
    {
        lock (_sync)
        {
            var cycles = new Dictionary<string, IReadOnlyList<AssetId>>(StringComparer.Ordinal);
            var visiting = new HashSet<AssetId>();
            var visited = new HashSet<AssetId>();
            var path = new List<AssetId>();
            foreach (var asset in _dependencies.Keys)
                Visit(asset, visiting, visited, path, cycles);
            return cycles.Values.ToArray();
        }
    }

    /// <summary>Traverses one dependency branch and records back-edge cycles.</summary>
    /// <param name="asset">Current asset identity.</param>
    /// <param name="visiting">Assets on the active traversal stack.</param>
    /// <param name="visited">Completed assets.</param>
    /// <param name="path">Active traversal path.</param>
    /// <param name="cycles">Cycles indexed by canonical member key.</param>
    private void Visit(
        AssetId asset,
        HashSet<AssetId> visiting,
        HashSet<AssetId> visited,
        List<AssetId> path,
        Dictionary<string, IReadOnlyList<AssetId>> cycles)
    {
        if (visited.Contains(asset))
            return;
        if (!visiting.Add(asset))
        {
            var start = path.IndexOf(asset);
            if (start >= 0)
            {
                var cycle = path.Skip(start).OrderBy(id => id.ToString(), StringComparer.Ordinal)
                    .ToArray();
                cycles.TryAdd(string.Join("|", cycle), cycle);
            }
            return;
        }
        path.Add(asset);
        if (_dependencies.TryGetValue(asset, out var dependencies))
        {
            foreach (var dependency in dependencies)
                Visit(dependency, visiting, visited, path, cycles);
        }
        path.RemoveAt(path.Count - 1);
        visiting.Remove(asset);
        visited.Add(asset);
    }

    /// <summary>Creates a deterministic immutable copy of one identity set.</summary>
    /// <param name="values">Identity set or null.</param>
    /// <returns>Ordered identity snapshot.</returns>
    private static IReadOnlyList<AssetId> Snapshot(HashSet<AssetId>? values)
    {
        return values is null ? [] : values.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
    }
}
