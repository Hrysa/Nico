using Microsoft.Extensions.DependencyInjection;

namespace Nico.Core;

public static class ClientBuilderExtensions
{
    public static IClientBuilder UseLocalhostClustering(
        this IClientBuilder builder,
        int gatewayPort = 20000)
    {
        builder.Services.AddSingleton<IGatewayListProvider, StaticGatewayListProvider>();
        builder.Services.AddHostedService<ClusterClient>();
        return builder;
    }
}
