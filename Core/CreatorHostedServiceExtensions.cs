using Microsoft.Extensions.DependencyInjection;

namespace Nico.Core;

public static class CreatorHostedServiceExtensions
{
    public static IServiceCollection AddCreatorHostedService(this IServiceCollection services)
    {
        services.AddHostedService<CreatorHostedService>();

        return services;
    }
}
