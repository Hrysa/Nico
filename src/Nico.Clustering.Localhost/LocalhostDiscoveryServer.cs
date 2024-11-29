using Microsoft.Extensions.Options;
using Nico.Discovery.Abstractions;

namespace Nico.Discovery;

public class LocalhostDiscoveryServer : IDiscoveryServer
{
    private readonly IOptions<LocalHostClusteringOptions> _options;

    private readonly Dictionary<int, LocalhostDiscoveryClientConfig> _client = new();

    public LocalhostDiscoveryServer(IOptions<LocalHostClusteringOptions> options)
    {
        _options = options;
        Console.WriteLine(_options.Value);
    }

    public void AddClient(LocalhostDiscoveryClientConfig config)
    {
        _client[config.Id] = config;
    }

    public void RemoveClient(LocalhostDiscoveryClientConfig config)
    {
        _client.Remove(config.Id);
    }
}
