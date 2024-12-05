using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nico.Core;

namespace Nico.Client;

public static class NicoClientHostExtensions
{
    public static IHostBuilder UseNicoClient(this IHostBuilder hostBuilder, Action<IClientBuilder> configureDelegate)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);
        ArgumentNullException.ThrowIfNull(configureDelegate);

        return hostBuilder.ConfigureServices((ctx, services) => configureDelegate(AddNicoClient(services)));
    }

    private static IClientBuilder AddNicoClient(IServiceCollection services)
    {
        IClientBuilder clientBuilder = new ClientBuilder(services);
        services.AddSingleton(clientBuilder);

        return clientBuilder;
    }
}
