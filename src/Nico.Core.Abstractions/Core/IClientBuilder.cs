using Microsoft.Extensions.DependencyInjection;

namespace Nico.Core;

public interface IClientBuilder
{
    /// <summary>
    /// Gets the services collection.
    /// </summary>
    IServiceCollection Services { get; }
}
