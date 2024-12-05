using Microsoft.Extensions.DependencyInjection;

namespace Nico.Core;

public class ClientBuilder(IServiceCollection services) : IClientBuilder
{
    /// <summary>
    /// Gets the services collection.
    /// </summary>
    public IServiceCollection Services { get; } = services;
}
