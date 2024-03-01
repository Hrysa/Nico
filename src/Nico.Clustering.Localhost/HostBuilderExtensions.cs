using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nico.Discovery.Abstractions;

namespace Nico.Discovery;

public static class HostBuilderExtensions
{
    public static IHostBuilder UseLocalhostClustering(this IHostBuilder builder,
        Action<LocalHostClusteringOptions> configureOptions)
    {
        builder.ConfigureServices(x =>
        {
            // x.AddSingleton(new LocalHostClusteringOptions());

            x.Configure<LocalHostClusteringOptions>(configureOptions);


            x.AddSingleton<IDiscovery, LocalhostDiscovery>();
        });
        return builder;
    }
}
