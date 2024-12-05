using Microsoft.Extensions.DependencyInjection;

namespace Nico.Runtime.Hosting;

public interface IStageBuilder
{
    IServiceCollection Services { get; }
}
