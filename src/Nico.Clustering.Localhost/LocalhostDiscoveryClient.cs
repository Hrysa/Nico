using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Nico.Discovery.Abstractions;

namespace Nico.Discovery;

public class LocalhostDiscoveryClient : IDiscoveryClient
{
    private readonly IOptions<LocalHostClusteringOptions> _options;
    private readonly LocalhostDiscoveryServer _discoveryServer;
    private readonly LocalhostDiscoveryClientConfig Current = new();

    public LocalhostDiscoveryClient(IOptions<LocalHostClusteringOptions> options,
        LocalhostDiscoveryServer discoveryServer)
    {
        _options = options;
        _discoveryServer = discoveryServer;
        Console.WriteLine(_options.Value);
    }

    public void Connect()
    {
        var uuid = Guid.NewGuid();

        Console.WriteLine($"{uuid.ToString()} {uuid.ToString().Length} {uuid.ToByteArray().Length} {Marshal.SizeOf<long>()} ");
        _discoveryServer.AddClient(Current);
    }

    public void Disconnect()
    {
        _discoveryServer.RemoveClient(Current);
    }
}
