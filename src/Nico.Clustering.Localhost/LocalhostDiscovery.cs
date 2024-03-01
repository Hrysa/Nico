using Microsoft.Extensions.Options;
using Nico.Discovery.Abstractions;

namespace Nico.Discovery;

public class LocalhostDiscovery : IDiscovery
{
    private readonly IOptions<LocalHostClusteringOptions> _options;

    public LocalhostDiscovery(IOptions<LocalHostClusteringOptions> options)
    {
        _options = options;
        Console.WriteLine(_options.Value);
    }
}
