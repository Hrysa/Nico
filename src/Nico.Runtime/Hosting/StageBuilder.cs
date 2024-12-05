using Microsoft.Extensions.DependencyInjection;

namespace Nico.Runtime.Hosting;

public class StageBuilder : IStageBuilder
{
    public IServiceCollection Services { get; }
    public StageBuilder(IServiceCollection services)
    {
        Services = services;

        AddDefaultServices();
    }

    private void AddDefaultServices()
    {
        Services.AddSingleton<Stage>();
        Services.AddHostedService<StageHostedService>();
    }

}
