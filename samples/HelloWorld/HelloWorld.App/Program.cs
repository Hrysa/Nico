using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nico.Core;
using Nico.Discovery;

await new HostBuilder()
    .ConfigureServices(x =>
    {
        //
        x.AddHostedService<NicoHostedService>();
    })
    .UseLocalhostClustering(x =>
    {
        Console.WriteLine("????" + (x == null));
    }).Build().RunAsync();
