using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Nico.Runtime.Hosting;

public static class StageGenericHostExtensions
{
    public static IHostBuilder UseNico(
        this IHostBuilder hostBuilder,
        Action<IStageBuilder> configureDelegate)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);
        ArgumentNullException.ThrowIfNull(configureDelegate);

        return hostBuilder.ConfigureServices((_, services) =>
            configureDelegate(AddServices(services)));
    }

    private static IStageBuilder AddServices(IServiceCollection services)
    {
        IStageBuilder builder = new StageBuilder(services);
        services.AddSingleton(builder);

        return builder;
    }
}
